using System.Collections.Immutable;
using FluentValidation;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Application.Shares.Create;

/// <summary>
/// <c>mysync sync</c> in a folder with no <c>.mysynctool</c> (CLAUDE.md §9): a new share from
/// everything in the folder. Confirmation for large folders happens in the command, before this.
/// </summary>
/// <param name="Name">Required: the share's name (<c>--name</c>). Claimed before anything is scanned or uploaded.</param>
/// <param name="Force">Allow the home directory or a drive root.</param>
public sealed record CreateShareCommand(
    ShareName? Name = null,
    bool Force = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null,
    bool Compress = false) : ICommand<CreateShareResponse>;

/// <param name="FileCount">Files in the new share (skipped ones excluded).</param>
/// <param name="Compression">The new share's setting: gzip with <c>--compress</c>, else none.</param>
public sealed record CreateShareResponse(
    ShareId ShareId,
    ShareName Name,
    int FileCount,
    long TotalBytes,
    TransferTotals Uploaded,
    ImmutableArray<SkippedPath> Skipped,
    ImmutableArray<string> Warnings,
    CompressionSetting Compression = CompressionSetting.None);

internal sealed class CreateShareCommandValidator : AbstractValidator<CreateShareCommand>
{
    public CreateShareCommandValidator() => RuleFor(command => command.Parallelism).ValidParallelism();
}

internal sealed class CreateShareCommandHandler(
    IFolderLock folderLock,
    IShareStateStore stateStore,
    IShareStore store,
    ILocalFolder folder,
    IFileScanner scanner,
    IIgnoreRulesLoader ignoreRules,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    IAliasStore aliases,
    ObjectTransfers transfers,
    RemoteCommitter committer,
    SyncOptions options,
    ILogger<CreateShareCommandHandler> logger)
    : ICommandHandler<CreateShareCommand, CreateShareResponse>
{
    public async Task<Result<CreateShareResponse>> Handle(CreateShareCommand command, CancellationToken cancellationToken)
    {
        ISyncProgressReporter progress = command.Progress ?? NullSyncProgressReporter.Instance;

        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState?> existing = await stateStore.LoadAsync(cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(existing.Error);
        }

        if (existing.Value is { } state)
        {
            return Result.Failure<CreateShareResponse>(ShareErrors.AlreadyAShare(state.ShareId));
        }

        if (folder.IsUnsafeRoot && !command.Force)
        {
            return Result.Failure<CreateShareResponse>(ShareErrors.UnsafeRoot(folder.RootPath));
        }

        // A name is required, and nothing is scanned or uploaded without one.
        if (command.Name is not { } name)
        {
            return Result.Failure<CreateShareResponse>(ShareErrors.NameRequired(ShareName.SuggestFrom(Path.GetFileName(folder.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))));
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(location.Error);
        }

        Result ready = await store.EnsureReadyForNewShareAsync(cancellationToken);
        if (ready.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(ready.Error);
        }

        // The name first: claiming it is the uniqueness check (If-None-Match: *). If anything after this
        // fails or is cancelled, the name stays and the share shows up as incomplete (delete frees it).
        var shareId = ShareId.New();
        Result claimed = await ClaimAsync(name, shareId, cancellationToken);
        if (claimed.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(claimed.Error);
        }

        await folder.CleanStagingAsync(cancellationToken);

        Result<IIgnoreMatcher> ignore = await ignoreRules.LoadAsync(cancellationToken);
        if (ignore.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(ignore.Error);
        }

        progress.PhaseStarted(SyncPhase.Scanning);
        Result<ScannedFolder> scanned = await scanner.ScanAsync(
            new ScanRequest(
                ignore.Value,
                ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
                ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer)),
            progress,
            cancellationToken);
        if (scanned.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(scanned.Error);
        }

        ScannedFolder scan = scanned.Value;
        List<string> warnings = [.. PathRules.FindCaseCollisions(scan.Entries.Keys).Select(collision =>
            $"These local paths differ only in case and will clash on Windows and macOS: {string.Join(", ", collision.Spellings.Select(PathErrors.Display))}")];

        CompressionSetting compression = command.Compress ? CompressionSetting.Gzip : CompressionSetting.None;
        UploadOutcome upload = await transfers.UploadAsync(
            shareId,
            [.. scan.Entries.Where(pair => pair.Value is FileEntry).Select(pair => (pair.Key, (FileEntry)pair.Value))],
            compression,
            progress,
            command.Parallelism ?? options.MaxParallelism,
            cancellationToken);

        // A file that couldn't be uploaded isn't part of the share yet; the next sync adds it.
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries = scan.Entries.Where(pair => !upload.Failed.ContainsKey(pair.Key));

        progress.PhaseStarted(SyncPhase.Committing);
        Result<Manifest> manifest = Manifest.CreateNew(shareId, entries, new DateTimeOffset(clock.UtcNow, TimeSpan.Zero), machine.Name, compression);
        if (manifest.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(manifest.Error);
        }

        Result<string> created = await store.CreateManifestAsync(shareId, manifest.Value, cancellationToken);
        if (created.IsFailure)
        {
            return Result.Failure<CreateShareResponse>(created.Error);
        }

        if (!await committer.WriteHistoryAsync(shareId, manifest.Value, cancellationToken))
        {
            warnings.Add("The share was created, but the history copy of revision 1 could not be written.");
        }

        ImmutableSortedDictionary<SharePath, HashCacheEntry> cache = scan.HashCache.RemoveRange(upload.Failed.Keys);
        Result saved = await stateStore.SaveAsync(
            ShareState.Create(location.Value, shareId, manifest.Value.Revision, manifest.Value.Entries, cache, []), cancellationToken);
        if (saved.IsFailure)
        {
            // The share exists remotely, but this folder doesn't know it: say which one it was.
            logger.LogError("Share {ShareId} was created but {StateFile} could not be written", shareId, StateFileNames.State);
            return Result.Failure<CreateShareResponse>(saved.Error);
        }

        await folder.CleanStagingAsync(cancellationToken);

        ImmutableArray<SkippedPath> skipped = [.. scan.Skipped
            .Select(local => new SkippedPath(local.Path, SkipCause.Unstable, local.Detail))
            .Concat(upload.Failed.Values)
            .OrderBy(skip => skip.Path, StringComparer.Ordinal)];

        return new CreateShareResponse(shareId, name, manifest.Value.FileCount, manifest.Value.TotalSize, upload.Uploaded, skipped, [.. warnings], compression);
    }

    private Task<Result> ClaimAsync(ShareName name, ShareId shareId, CancellationToken cancellationToken) =>
        ShareNameClaim.ClaimAsync(aliases, name, shareId, new DateTimeOffset(clock.UtcNow, TimeSpan.Zero), machine.Name, cancellationToken);
}
