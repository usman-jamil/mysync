using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Publishing.Publish;

/// <summary>
/// <c>mysync publish [--share &lt;id&gt;] [--revision &lt;n&gt;] [--title &lt;text&gt;]</c>: a public, read-only
/// copy of a snapshot of the share, as a static website on the publishing account (CLAUDE.md, Publishing).
/// </summary>
/// <param name="Revision">A revision from the share's history; <c>null</c> for HEAD.</param>
/// <param name="Title">Shown on the page; <c>null</c> for this folder's name.</param>
public sealed record PublishShareCommand(string? ShareId = null, long? Revision = null, string? Title = null, int? Parallelism = null)
    : ICommand<PublishReport>;

/// <summary>
/// The outcome. A publication that's up but couldn't be seen anonymously is still a success with
/// <see cref="Verified"/> false: the CLI exits 10 and points at <c>MYSYNC_PUBLISH_BASE_URL</c>.
/// </summary>
/// <param name="Copies">How many files went by each method.</param>
/// <param name="Skipped">Share paths that can't be blob names, left out of the publication.</param>
public sealed record PublishReport(
    ShareId ShareId,
    PublishId PublishId,
    Uri Url,
    Revision Revision,
    string Title,
    int Files,
    long Bytes,
    ImmutableDictionary<CopyMethod, int> Copies,
    bool Verified,
    string? VerificationDetail,
    ImmutableArray<UnpublishablePath> Skipped,
    ImmutableArray<string> Warnings)
{
    public string ShellCommand => $"curl -fsSL {Url.AbsoluteUri}{PublicationLayout.ShellScript} | sh";

    public string PowerShellCommand => $"irm {Url.AbsoluteUri}{PublicationLayout.PowerShellScript} | iex";
}

internal sealed class PublishShareCommandValidator : AbstractValidator<PublishShareCommand>
{
    public PublishShareCommandValidator()
    {
        RuleFor(command => command.ShareId)
            .Must(id => id is null || Domain.Shares.ShareId.TryParse(id, out _))
            .WithErrorCode(ShareErrors.InvalidId().Code)
            .WithMessage(ShareErrors.InvalidId().Description);

        RuleFor(command => command.Revision)
            .Must(revision => revision is null or >= 1)
            .WithMessage("--revision is a revision number, 1 or more.");

        RuleFor(command => command.Title)
            .Must(title => title is null || PublishShareCommandHandler.IsValidTitle(title))
            .WithErrorCode(PublishErrors.InvalidTitle().Code)
            .WithMessage(PublishErrors.InvalidTitle().Description);

        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class PublishShareCommandHandler(
    PublishingShare share,
    PublicationRegistryUpdater registry,
    IShareStore store,
    IPublishTarget target,
    ISiteAssetSource assets,
    ISiteDataSerializer serializer,
    IPublicationVerifier verifier,
    IContentHasher hasher,
    ILocalFolder folder,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    SyncPipeline pipeline,
    SyncOptions options,
    ILogger<PublishShareCommandHandler> logger)
    : ICommandHandler<PublishShareCommand, PublishReport>
{
    private const int MaxTitleLength = 200;

    /// <summary>How long the read-only URL of each source object stays valid; one copy needs seconds.</summary>
    private static readonly TimeSpan SourceUrlLifetime = TimeSpan.FromHours(1);

    private static readonly PublishedBlob PageHeaders = new("text/html; charset=utf-8", CacheControl: "no-cache");

    public static bool IsValidTitle(string title) =>
        title.Trim().Length is > 0 and <= MaxTitleLength && !title.Any(char.IsControl);

    public async Task<Result<PublishReport>> Handle(PublishShareCommand command, CancellationToken cancellationToken)
    {
        List<string> warnings = [];

        // Preflight, before anything is written anywhere.
        Result<ResolvedShare> resolved = await share.ResolveAsync(command.ShareId, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<PublishReport>(resolved.Error);
        }

        ShareId shareId = resolved.Value.ShareId;
        Result<PublishLocation> location = target.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<PublishReport>(location.Error);
        }

        Result<StorageLocation> privateLocation = store.GetLocation();
        if (privateLocation.IsFailure)
        {
            return Result.Failure<PublishReport>(privateLocation.Error);
        }

        if (string.Equals(privateLocation.Value.AccountName, location.Value.AccountName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Publishing to '{location.Value.AccountName}', the account that holds your private shares: the public URL names it. They stay private, but a separate publishing account names nothing.");
        }

        Result website = await target.CheckStaticWebsiteAsync(cancellationToken);
        if (website.IsFailure)
        {
            return Result.Failure<PublishReport>(website.Error);
        }

        if (assets.IsPlaceholder)
        {
            return Result.Failure<PublishReport>(PublishErrors.SiteAssetsMissing);
        }

        Result<Manifest> snapshot = await LoadSnapshotAsync(shareId, command.Revision, cancellationToken);
        if (snapshot.IsFailure)
        {
            return Result.Failure<PublishReport>(snapshot.Error);
        }

        if (resolved.Value.Here is { } here && command.Revision is null)
        {
            await WarnAboutLocalChangesAsync(here, snapshot.Value.Revision, warnings, cancellationToken);
        }

        // What will be published.
        var id = PublishId.New();
        var layout = PublicationLayout.For(id);
        Uri url = layout.Url(location.Value.BaseUrl);
        string title = command.Title?.Trim() ?? DefaultTitle(resolved.Value.Here is not null);
        DateTimeOffset now = new(clock.UtcNow, TimeSpan.Zero);
        (SiteData data, ImmutableArray<UnpublishablePath> skipped) = SiteDataBuilder.Build(
            snapshot.Value,
            new SiteDataPublication(id, title, now, snapshot.Value.Revision, Generator()));

        Publication record = new(
            id, PublicationStatus.Pending, title, snapshot.Value.Revision, (int)data.TotalFiles, data.TotalBytes,
            now, machine.Name, location.Value.AccountName, url.AbsoluteUri, assets.Version);

        // 1. The record first, so whatever follows can always be found and removed.
        Result<PublicationRegistry> pending = await registry.UpdateAsync(shareId, current => current.With(record), cancellationToken);
        if (pending.IsFailure)
        {
            return Result.Failure<PublishReport>(pending.Error);
        }

        Result<ImmutableDictionary<CopyMethod, int>> uploaded;
        try
        {
            uploaded = await UploadAsync(shareId, layout, data, url, command.Parallelism ?? options.MaxParallelism, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbandonAsync(shareId, layout, record);
            throw;
        }

        if (uploaded.IsFailure)
        {
            await AbandonAsync(shareId, layout, record);
            return Result.Failure<PublishReport>(uploaded.Error);
        }

        // 8. Published: the link works.
        Result<PublicationRegistry> published = await registry.UpdateAsync(
            shareId, current => current.With(record with { Status = PublicationStatus.Published }), CancellationToken.None);
        if (published.IsFailure)
        {
            warnings.Add($"The publication is up, but its record couldn't be marked published ({published.Error.Description}). `mysync publications` may show it as pending.");
        }

        // 9. As anyone with the link sees it.
        VerificationOutcome verified = await verifier.VerifyAsync(new Uri(url, PublicationLayout.SiteData), id.Value, cancellationToken);

        return new PublishReport(
            shareId, id, url, snapshot.Value.Revision, title, (int)data.TotalFiles, data.TotalBytes,
            uploaded.Value, verified.Verified, verified.Detail, skipped, [.. warnings]);
    }

    private async Task<Result<Manifest>> LoadSnapshotAsync(ShareId shareId, long? revision, CancellationToken cancellationToken)
    {
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<Manifest>(head.Error);
        }

        if (head.Value is not { } current)
        {
            return Result.Failure<Manifest>(ShareErrors.NotFound(shareId));
        }

        if (current.Manifest.IsDeleted)
        {
            return Result.Failure<Manifest>(ShareErrors.Deleted(shareId, current.Manifest.Deletion!));
        }

        if (revision is null || revision == current.Manifest.Revision.Value)
        {
            return current.Manifest;
        }

        await foreach (Result<Manifest> history in store.ListManifestHistoryAsync(shareId, cancellationToken))
        {
            if (history.IsSuccess && history.Value.Revision.Value == revision && !history.Value.IsDeleted)
            {
                return history.Value;
            }
        }

        return Result.Failure<Manifest>(PublishErrors.RevisionNotFound(revision.Value));
    }

    // Read-only, like `status`: nothing is written, and a failure here only costs the warning.
    private async Task WarnAboutLocalChangesAsync(ShareState here, Revision published, List<string> warnings, CancellationToken cancellationToken)
    {
        Result<SyncReport> preview = await pipeline.PreviewAsync(
            new SyncSession(
                here.ShareId, here.Location, here.Base, here.HashCache, here.Conflicts, MergeOptions.Default,
                AllowMassDelete: false, DryRun: true, NullSyncProgressReporter.Instance, options.MaxParallelism),
            cancellationToken);

        if (preview.IsFailure)
        {
            logger.LogWarning("Could not check this folder for unsynced changes: {Error}", preview.Error.Code);
            return;
        }

        if (preview.Value.Plan.ChangesRemote || !preview.Value.Conflicts.IsEmpty)
        {
            warnings.Add($"This folder has changes that aren't synced. The publication is revision {published}, the last synced state; run `mysync sync` and publish again to include them.");
        }
    }

    private async Task<Result<ImmutableDictionary<CopyMethod, int>>> UploadAsync(
        ShareId shareId, PublicationLayout layout, SiteData data, Uri url, int parallelism, CancellationToken cancellationToken)
    {
        // 2. The website root, once per account; an existing file is left alone.
        foreach (SiteAsset asset in assets.RootAssets)
        {
            Result<bool> root = await PutAssetAsync(asset.Path, asset, onlyIfAbsent: true, cancellationToken);
            if (root.IsFailure)
            {
                return Result.Failure<ImmutableDictionary<CopyMethod, int>>(root.Error);
            }
        }

        // 3-4. Every file, server-side where possible, with its headers.
        Result<ImmutableDictionary<CopyMethod, int>> copied = await CopyFilesAsync(shareId, layout, data, parallelism, cancellationToken);
        if (copied.IsFailure)
        {
            return copied;
        }

        // 5. The page's assets, except index.html.
        foreach (SiteAsset asset in assets.PublicationAssets.Where(asset => asset.Path != PublicationLayout.IndexDocument))
        {
            Result<bool> put = await PutAssetAsync(layout.Blob(asset.Path), asset, onlyIfAbsent: false, cancellationToken);
            if (put.IsFailure)
            {
                return Result.Failure<ImmutableDictionary<CopyMethod, int>>(put.Error);
            }
        }

        // 6. What this publication is: the data, the checksums, the download scripts.
        (string Name, byte[] Bytes, PublishedBlob Headers)[] generated =
        [
            (PublicationLayout.SiteData, serializer.Serialize(data), new PublishedBlob("text/javascript; charset=utf-8", CacheControl: "no-cache")),
            (PublicationLayout.Sha256Sums, Encoding.UTF8.GetBytes(DownloadScripts.Sha256Sums(data)), new PublishedBlob("text/plain; charset=utf-8", CacheControl: "no-cache")),
            (PublicationLayout.ShellScript, Encoding.UTF8.GetBytes(DownloadScripts.Shell(data, url)), new PublishedBlob("text/plain; charset=utf-8", CacheControl: "no-cache")),
            (PublicationLayout.PowerShellScript, Encoding.UTF8.GetBytes(DownloadScripts.PowerShell(data, url)), new PublishedBlob("text/plain; charset=utf-8", CacheControl: "no-cache")),
        ];
        foreach ((string name, byte[] bytes, PublishedBlob headers) in generated)
        {
            using MemoryStream content = new(bytes, writable: false);
            Result<bool> put = await target.PutAsync(layout.Blob(name), content, headers, onlyIfAbsent: false, cancellationToken);
            if (put.IsFailure)
            {
                return Result.Failure<ImmutableDictionary<CopyMethod, int>>(put.Error);
            }
        }

        // 7. index.html last: the commit point. Until it exists, the link shows nothing.
        SiteAsset index = assets.PublicationAssets.Single(asset => asset.Path == PublicationLayout.IndexDocument);
        await using (Stream content = assets.Open(index))
        {
            Result<bool> committed = await target.PutAsync(layout.Index, content, PageHeaders, onlyIfAbsent: true, cancellationToken);
            if (committed.IsFailure)
            {
                return Result.Failure<ImmutableDictionary<CopyMethod, int>>(committed.Error);
            }
        }

        return copied;
    }

    private async Task<Result<bool>> PutAssetAsync(string blobName, SiteAsset asset, bool onlyIfAbsent, CancellationToken cancellationToken)
    {
        await using Stream content = assets.Open(asset);
        string cacheControl = asset.Path.EndsWith(".html", StringComparison.Ordinal) ? "no-cache" : PublishedBlob.Immutable;
        return await target.PutAsync(blobName, content, new PublishedBlob(asset.ContentType, CacheControl: cacheControl), onlyIfAbsent, cancellationToken);
    }

    private async Task<Result<ImmutableDictionary<CopyMethod, int>>> CopyFilesAsync(
        ShareId shareId, PublicationLayout layout, SiteData data, int parallelism, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<CopyMethod, int> counts = new();
        Error? first = null;
        Lock gate = new();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                data.Entries.OfType<SiteDataFile>(),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stop.Token },
                async (file, token) =>
                {
                    Result<CopyMethod> copied = await CopyFileAsync(shareId, layout, file, token);
                    if (copied.IsSuccess)
                    {
                        counts.AddOrUpdate(copied.Value, 1, (_, count) => count + 1);
                        return;
                    }

                    lock (gate)
                    {
                        first ??= copied.Error;
                    }

                    await stop.CancelAsync();
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && first is not null)
        {
            // Stopped by the first failure; it's reported below.
        }

        return first is null ? counts.ToImmutableDictionary() : Result.Failure<ImmutableDictionary<CopyMethod, int>>(first);
    }

    private async Task<Result<CopyMethod>> CopyFileAsync(ShareId shareId, PublicationLayout layout, SiteDataFile file, CancellationToken cancellationToken)
    {
        PublishedBlob headers = new(ContentTypeMap.For(file.Path.Value), DispositionPolicy.ContentDisposition(file.Path.Value), PublishedBlob.Immutable);
        string blobName = layout.File(file.Path);

        Result<Uri?> source = await store.GetObjectReadUrlAsync(shareId, file.Sha256, SourceUrlLifetime, cancellationToken);
        if (source.IsFailure)
        {
            return Result.Failure<CopyMethod>(source.Error);
        }

        if (source.Value is { } readable)
        {
            return await target.CopyAsync(readable, file.Size, blobName, headers, cancellationToken);
        }

        // No readable URL: through this machine, checked against the object's name on the way.
        Result<Stream> opened = await store.OpenObjectReadAsync(shareId, file.Sha256, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<CopyMethod>(opened.Error);
        }

        await using Stream content = opened.Value;
        using IIncrementalContentHash hash = hasher.CreateIncremental();
        await using HashingReadStream hashing = new(content, hash);
        Result<bool> put = await target.PutAsync(blobName, hashing, headers, onlyIfAbsent: false, cancellationToken);
        if (put.IsFailure)
        {
            return Result.Failure<CopyMethod>(put.Error);
        }

        if (hash.Finish() != file.Sha256)
        {
            await target.DeleteAsync(blobName, CancellationToken.None);
            return Result.Failure<CopyMethod>(StorageErrors.HashMismatch(blobName));
        }

        return CopyMethod.Streamed;
    }

    // 10. Before the commit point: take down what was uploaded (best effort) and say so in the record.
    private async Task AbandonAsync(ShareId shareId, PublicationLayout layout, Publication record)
    {
        Result<int> cleaned = await target.DeletePrefixAsync(layout.Prefix, CancellationToken.None);
        if (cleaned.IsFailure)
        {
            logger.LogWarning("Could not remove the partial publication {Id}: {Error}", record.Id, cleaned.Error.Code);
        }

        Result<PublicationRegistry> failed = await registry.UpdateAsync(
            shareId, current => current.With(record with { Status = PublicationStatus.Failed }), CancellationToken.None);
        if (failed.IsFailure)
        {
            logger.LogWarning("Could not mark publication {Id} as failed: {Error}", record.Id, failed.Error.Code);
        }
    }

    private string DefaultTitle(bool thisFolder)
    {
        string name = thisFolder ? Path.GetFileName(folder.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : string.Empty;
        return IsValidTitle(name) ? name : "Shared files";
    }

    private static string Generator() =>
        "mysync " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");
}

/// <summary>Hashes what passes through it while reading, for the streamed copy.</summary>
internal sealed class HashingReadStream(Stream inner, IIncrementalContentHash hash) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        hash.Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        hash.Append(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
