using FluentValidation;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Updates;
using MySync.Domain.Content;
using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Application.Maintenance.Update;

/// <summary>
/// <c>mysync update</c>: replace this binary with a newer (or a chosen) release, verified end to end.
/// Never runs on its own: mysync doesn't contact the release source unless asked.
/// </summary>
/// <remarks>
/// In order, and nothing is replaced until the last step:
/// <list type="number">
/// <item>Pick the release: the latest (no prereleases unless asked), or <see cref="Version"/>.</item>
/// <item>Check the binary can be replaced at all, before downloading anything.</item>
/// <item>Download <c>SHA256SUMS.txt</c> and its signature; the signature must verify against the release key.</item>
/// <item>Download this platform's archive, hashing it as it arrives; the hash must be the signed one.</item>
/// <item>Extract the executable and run it: <c>--version</c> must print the release, <c>self-check</c> must pass.</item>
/// <item>Swap it in.</item>
/// </list>
/// </remarks>
/// <param name="CheckOnly"><c>--check</c>: report whether an update exists; download nothing.</param>
/// <param name="Version"><c>--version</c>: install exactly this release, even an older one (to keep a team on one version).</param>
/// <param name="IncludePrereleases"><c>--prerelease</c>: let "latest" be a prerelease.</param>
public sealed record UpdateCommand(
    bool CheckOnly = false,
    string? Version = null,
    bool IncludePrereleases = false,
    ISyncProgressReporter? Progress = null) : ICommand<UpdateReport>;

public enum UpdateStatus
{
    /// <summary>This binary already is the chosen release.</summary>
    UpToDate = 0,

    /// <summary><c>--check</c>: a different release would be installed.</summary>
    Available = 1,

    /// <summary>The new release is installed; it takes effect on the next run.</summary>
    Updated = 2,
}

/// <param name="Target">The release chosen: the latest, or <c>--version</c>.</param>
/// <param name="SignificantChange">The major or minor number changes: read the notes, and update every machine in the team.</param>
public sealed record UpdateReport(
    UpdateStatus Status,
    ReleaseVersion Current,
    ReleaseVersion Target,
    bool Downgrade,
    bool SignificantChange,
    string? NotesUrl,
    string ExecutablePath,
    string Source);

/// <summary>The key releases must be signed with. The CLI can embed another at build time (test builds only).</summary>
public sealed record UpdateOptions(string PublicKeyPem)
{
    public static UpdateOptions Production { get; } = new(ReleaseSignature.ProductionPublicKeyPem);
}

internal sealed class UpdateCommandValidator : AbstractValidator<UpdateCommand>
{
    public UpdateCommandValidator() =>
        RuleFor(command => command.Version)
            .Must(version => version is null || ReleaseVersion.TryParse(version, out _))
            .WithErrorCode(UpdateErrors.InvalidVersion().Code)
            .WithMessage(UpdateErrors.InvalidVersion().Description);
}

internal sealed class UpdateCommandHandler(IReleaseSource source, IExecutableInstaller installer, IContentHasher hasher, UpdateOptions options)
    : ICommandHandler<UpdateCommand, UpdateReport>
{
    public async Task<Result<UpdateReport>> Handle(UpdateCommand command, CancellationToken cancellationToken)
    {
        InstalledExecutable installed = installer.Locate();

        Result<ReleaseInfo> chosen = command.Version is { } wanted && ReleaseVersion.TryParse(wanted, out ReleaseVersion? pinned)
            ? await source.GetAsync(pinned, cancellationToken)
            : await source.GetLatestAsync(command.IncludePrereleases, cancellationToken);
        if (chosen.IsFailure)
        {
            return Result.Failure<UpdateReport>(chosen.Error);
        }

        ReleaseInfo release = chosen.Value;
        bool pinnedVersion = command.Version is not null;

        // "Latest" never goes backwards; only an explicit --version installs an older release.
        bool same = release.Version.CompareTo(installed.Version) == 0;
        bool older = release.Version < installed.Version;
        if (same || older && !pinnedVersion)
        {
            return Report(UpdateStatus.UpToDate, installed, release, downgrade: false);
        }

        if (command.CheckOnly)
        {
            return Report(UpdateStatus.Available, installed, release, older);
        }

        if (installed.CannotReplaceBecause is { } reason)
        {
            return Result.Failure<UpdateReport>(UpdateErrors.NotInstalledBinary(reason));
        }

        Result replaceable = installer.CheckReplaceable(installed);
        if (replaceable.IsFailure)
        {
            return Result.Failure<UpdateReport>(replaceable.Error);
        }

        Result installedNew = await InstallAsync(installed, release, command.Progress ?? NullSyncProgressReporter.Instance, cancellationToken);
        return installedNew.IsSuccess
            ? Report(UpdateStatus.Updated, installed, release, older)
            : Result.Failure<UpdateReport>(installedNew.Error);
    }

    private async Task<Result> InstallAsync(InstalledExecutable installed, ReleaseInfo release, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        string archiveName = ReleaseAssets.ArchiveName(release.Version, installed.RuntimeIdentifier);
        if (release.Find(archiveName) is not { } archive)
        {
            return Result.Failure(UpdateErrors.NoBuildForPlatform(release.Version, installed.RuntimeIdentifier));
        }

        if (release.Find(ReleaseAssets.ChecksumsName) is not { } checksums || release.Find(ReleaseAssets.SignatureName) is not { } signature)
        {
            return Result.Failure(UpdateErrors.SignatureMissing(release.Version));
        }

        Result<IUpdateWorkspace> created = await installer.CreateWorkspaceAsync(installed, cancellationToken);
        if (created.IsFailure)
        {
            return created;
        }

        await using IUpdateWorkspace workspace = created.Value;

        // The signed checksums first: nothing large is downloaded for a release that won't verify.
        Result<byte[]> sums = await DownloadSmallAsync(checksums, cancellationToken);
        Result<byte[]> signed = sums.IsSuccess ? await DownloadSmallAsync(signature, cancellationToken) : sums;
        if (signed.IsFailure)
        {
            return signed;
        }

        if (!ReleaseSignature.IsValid(sums.Value, signed.Value, options.PublicKeyPem))
        {
            return Result.Failure(UpdateErrors.SignatureInvalid);
        }

        if (!ReleaseAssets.ParseChecksums(System.Text.Encoding.UTF8.GetString(sums.Value)).TryGetValue(archiveName, out ContentHash? expected))
        {
            return Result.Failure(UpdateErrors.ChecksumMismatch(archiveName));
        }

        // The archive, hashed as it is written.
        progress.PhaseStarted(SyncPhase.Downloading);
        progress.TransfersPlanned(TransferDirection.Download, 1, archive.Size);
        using IIncrementalContentHash hash = hasher.CreateIncremental();
        Stream file = workspace.CreateFile(archiveName);
        await using (file)
        {
            await using HashingWriteStream hashing = new(file, hash, progress);
            Result downloaded = await source.DownloadAsync(archive, hashing, cancellationToken);
            if (downloaded.IsFailure)
            {
                return downloaded;
            }
        }

        if (hash.Finish() != expected)
        {
            return Result.Failure(UpdateErrors.ChecksumMismatch(archiveName));
        }

        Result<string> executable = await workspace.ExtractExecutableAsync(archiveName, ReleaseAssets.ExecutableName(installed.RuntimeIdentifier), cancellationToken);
        if (executable.IsFailure)
        {
            return executable;
        }

        // Prove it runs here before it replaces anything.
        Result tested = await TestAsync(workspace, executable.Value, release.Version, cancellationToken);
        return tested.IsFailure ? tested : await workspace.InstallAsync(executable.Value, installed, cancellationToken);
    }

    private static async Task<Result> TestAsync(IUpdateWorkspace workspace, string executable, ReleaseVersion expected, CancellationToken cancellationToken)
    {
        Result<ProcessOutcome> version = await workspace.RunAsync(executable, ["--version"], cancellationToken);
        if (version.IsFailure)
        {
            return version;
        }

        if (version.Value.ExitCode != 0
            || !ReleaseVersion.TryParse(version.Value.Output.Trim(), out ReleaseVersion? reported)
            || reported.CompareTo(expected) != 0)
        {
            return Result.Failure(UpdateErrors.NewBinaryFailed($"it reports version '{version.Value.Output.Trim()}', expected {expected}"));
        }

        Result<ProcessOutcome> check = await workspace.RunAsync(executable, ["self-check"], cancellationToken);
        if (check.IsFailure)
        {
            return check;
        }

        return check.Value.ExitCode == 0
            ? Result.Success()
            : Result.Failure(UpdateErrors.NewBinaryFailed($"self-check exited with {check.Value.ExitCode}"));
    }

    private async Task<Result<byte[]>> DownloadSmallAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        Result downloaded = await source.DownloadAsync(asset, buffer, cancellationToken);
        return downloaded.IsSuccess ? buffer.ToArray() : Result.Failure<byte[]>(downloaded.Error);
    }

    private UpdateReport Report(UpdateStatus status, InstalledExecutable installed, ReleaseInfo release, bool downgrade) =>
        new(status, installed.Version, release.Version, downgrade, release.Version.IsSignificantChangeFrom(installed.Version), release.NotesUrl, installed.Path, source.Description);
}

/// <summary>Write-through stream that hashes and reports progress as a download is written.</summary>
internal sealed class HashingWriteStream(Stream inner, IIncrementalContentHash hash, ISyncProgressReporter progress) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        hash.Append(buffer);
        inner.Write(buffer);
        progress.BytesTransferred(TransferDirection.Download, buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        hash.Append(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
        progress.BytesTransferred(TransferDirection.Download, buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
