using System.Collections.Immutable;
using FluentValidation;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Maintenance.CollectGarbage;

/// <summary>
/// <c>mysync gc</c> (CLAUDE.md §10), never automatic. Deletes object blobs that neither HEAD nor
/// any of the last <see cref="KeepRevisions"/> history manifests reference <b>and</b> that are older
/// than <see cref="GraceHours"/>, then prunes history manifests outside that window.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The grace period protects objects another machine uploaded for a commit it hasn't made yet.</item>
/// <item>Anything it can't read (a history manifest, the object listing) stops it before it deletes
/// anything: it never guesses what is referenced.</item>
/// <item>HEAD is read again just before deleting; if it moved, nothing is deleted
/// (<see cref="SyncErrors.ShareChangedDuringGc"/>). A sync that starts later and reuses an old,
/// unreferenced object is the remaining risk, so run gc when no one else is syncing.</item>
/// </list>
/// </remarks>
public sealed record CollectGarbageCommand(int KeepRevisions = CollectGarbageCommand.DefaultKeepRevisions, int GraceHours = CollectGarbageCommand.DefaultGraceHours, bool DryRun = false)
    : ICommand<GarbageReport>
{
    public const int DefaultKeepRevisions = 20;
    public const int DefaultGraceHours = 24;
}

/// <param name="Head">The revision gc planned against.</param>
/// <param name="KeptRevisions">HEAD plus the history manifests kept.</param>
/// <param name="DeletedObjects">Deleted (or, with <c>--dry-run</c>, to delete).</param>
/// <param name="ProtectedByGrace">Unreferenced but too recent to delete.</param>
/// <param name="DeletedHistory">History manifests pruned (or to prune).</param>
public sealed record GarbageReport(
    ShareId ShareId,
    bool DryRun,
    Revision Head,
    ImmutableArray<Revision> KeptRevisions,
    ImmutableArray<StoredObjectInfo> DeletedObjects,
    int KeptObjects,
    int ProtectedByGrace,
    ImmutableArray<Revision> DeletedHistory)
{
    public long DeletedBytes => DeletedObjects.Sum(item => item.Size);
}

internal sealed class CollectGarbageCommandValidator : AbstractValidator<CollectGarbageCommand>
{
    public CollectGarbageCommandValidator()
    {
        RuleFor(command => command.KeepRevisions)
            .InclusiveBetween(1, 100_000)
            .WithErrorCode("Maintenance.InvalidKeepRevisions")
            .WithMessage("--keep-revisions must be at least 1.");

        RuleFor(command => command.GraceHours)
            .InclusiveBetween(1, 24 * 365)
            .WithErrorCode("Maintenance.InvalidGraceHours")
            .WithMessage("--grace-hours must be at least 1: a shorter grace period could delete what another machine is about to commit.");
    }
}

internal sealed class CollectGarbageCommandHandler(IFolderLock folderLock, ShareFolder shareFolder, IShareStore store, IDateTimeProvider clock)
    : ICommandHandler<CollectGarbageCommand, GarbageReport>
{
    public async Task<Result<GarbageReport>> Handle(CollectGarbageCommand command, CancellationToken cancellationToken)
    {
        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<GarbageReport>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState> state = await shareFolder.LoadAsync(cancellationToken);
        if (state.IsFailure)
        {
            return Result.Failure<GarbageReport>(state.Error);
        }

        ShareId shareId = state.Value.ShareId;
        Result<Manifest> head = await HeadAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<GarbageReport>(head.Error);
        }

        // What is kept: HEAD and the newest history manifests.
        List<Manifest> history = [];
        await foreach (Result<Manifest> manifest in store.ListManifestHistoryAsync(shareId, cancellationToken))
        {
            if (manifest.IsFailure)
            {
                return Result.Failure<GarbageReport>(manifest.Error);
            }

            history.Add(manifest.Value);
        }

        List<Manifest> kept = [.. history.Where(manifest => manifest.Revision <= head.Value.Revision).OrderByDescending(manifest => manifest.Revision.Value).Take(command.KeepRevisions)];
        HashSet<long> keptRevisions = [head.Value.Revision.Value, .. kept.Select(manifest => manifest.Revision.Value)];
        HashSet<ContentHash> referenced = [.. kept.Prepend(head.Value).SelectMany(manifest => manifest.Entries.Values).OfType<FileEntry>().Select(file => file.Sha256)];
        ImmutableArray<Revision> prune = [.. history.Where(manifest => !keptRevisions.Contains(manifest.Revision.Value)).Select(manifest => manifest.Revision)];

        // What is garbage: unreferenced and older than the grace period.
        DateTimeOffset cutoff = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).AddHours(-command.GraceHours);
        List<StoredObjectInfo> garbage = [];
        int keptObjects = 0;
        int young = 0;
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(shareId, cancellationToken))
        {
            if (item.IsFailure)
            {
                return Result.Failure<GarbageReport>(item.Error);
            }

            if (referenced.Contains(item.Value.Hash))
            {
                keptObjects++;
            }
            else if (item.Value.LastModifiedUtc >= cutoff)
            {
                young++;
            }
            else
            {
                garbage.Add(item.Value);
            }
        }

        GarbageReport report = new(
            shareId,
            command.DryRun,
            head.Value.Revision,
            [.. keptRevisions.Order().Select(Rev)],
            [.. garbage],
            keptObjects,
            young,
            prune);

        if (command.DryRun || garbage.Count == 0 && prune.Length == 0)
        {
            return report;
        }

        // Last look before deleting: if someone committed meanwhile, the plan may be stale.
        Result<Manifest> again = await HeadAsync(shareId, cancellationToken);
        if (again.IsFailure)
        {
            return Result.Failure<GarbageReport>(again.Error);
        }

        if (again.Value.Revision != head.Value.Revision)
        {
            return Result.Failure<GarbageReport>(SyncErrors.ShareChangedDuringGc(head.Value.Revision.Value, again.Value.Revision.Value));
        }

        foreach (StoredObjectInfo item in garbage)
        {
            Result deleted = await store.DeleteObjectAsync(shareId, item.Hash, cancellationToken);
            if (deleted.IsFailure)
            {
                return Result.Failure<GarbageReport>(deleted.Error);
            }
        }

        foreach (Revision revision in prune)
        {
            Result deleted = await store.DeleteManifestHistoryAsync(shareId, revision, cancellationToken);
            if (deleted.IsFailure)
            {
                return Result.Failure<GarbageReport>(deleted.Error);
            }
        }

        return report;
    }

    private static Revision Rev(long value) => Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentOutOfRangeException(nameof(value));

    private async Task<Result<Manifest>> HeadAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<Manifest>(head.Error);
        }

        return head.Value is { } snapshot ? snapshot.Manifest : Result.Failure<Manifest>(ShareErrors.NotFound(shareId));
    }
}
