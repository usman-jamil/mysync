using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>
/// One sync of a folder against an existing share, phases 2–9 of CLAUDE.md §9. Shared by
/// <c>sync</c> (Run) and <c>sync &lt;id&gt;</c> (Clone); the handler has already taken the folder
/// lock and loaded the state (phase 1). The order makes a crash at any point safe to re-run.
/// </summary>
internal sealed class SyncPipeline(
    ILocalFolder folder,
    IFileScanner scanner,
    IIgnoreRulesLoader ignoreRules,
    IShareStateStore stateStore,
    IConflictLog conflictLog,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    RemoteCommitter committer,
    ObjectTransfers transfers,
    LocalApplier applier,
    ILogger<SyncPipeline> logger)
{
    public async Task<Result<SyncReport>> SynchronizeAsync(SyncSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ISyncProgressReporter progress = session.Progress;
        List<string> warnings = [];

        await CleanStagingAsync(warnings, cancellationToken);

        // Phase 2: scan.
        Result<IIgnoreMatcher> ignore = await ignoreRules.LoadAsync(cancellationToken);
        if (ignore.IsFailure)
        {
            return Result.Failure<SyncReport>(ignore.Error);
        }

        progress.PhaseStarted(SyncPhase.Scanning);
        Result<ScannedFolder> scanned = await scanner.ScanAsync(
            new ScanRequest(ignore.Value, session.HashCache, session.Base), progress, cancellationToken);
        if (scanned.IsFailure)
        {
            return Result.Failure<SyncReport>(scanned.Error);
        }

        ScannedFolder scan = scanned.Value;
        // Keyed by path text: a skipped local path may not be a valid SharePath (that may be why it was skipped).
        Dictionary<string, SkippedPath> skipped = new(StringComparer.Ordinal);
        List<SharePath> scanSkipped = [];
        foreach (SkippedLocalPath local in scan.Skipped)
        {
            skipped[local.Path] = new SkippedPath(local.Path, Map(local.Reason), local.Detail);
            if (SharePath.Create(local.Path) is { IsSuccess: true } valid)
            {
                scanSkipped.Add(valid.Value);
            }
        }

        warnings.AddRange(PathRules.FindCaseCollisions(scan.Entries.Keys).Select(collision =>
            $"These local paths differ only in case and will clash on Windows and macOS: {string.Join(", ", collision.Spellings.Select(PathErrors.Display))}"));

        // Phases 3–7: read remote, plan, guard, upload, commit (re-planned if someone else commits first).
        SyncPlan plan = null!;
        Dictionary<SharePath, SkippedPath> uploadFailures = [];
        TransferTotals uploaded = TransferTotals.None;

        Result<CommitOutcome> commit = await committer.CommitAsync(
            session.ShareId,
            async (snapshot, ct) =>
            {
                ImmutableSortedDictionary<SharePath, ManifestEntry> remote = snapshot.Manifest.Entries;
                (IReadOnlyDictionary<SharePath, ManifestEntry> local, HashSet<SharePath> frozen) =
                    FreezeUnscannable(session.Base, scan, scanSkipped, remote, ignore.Value, skipped);

                plan = MergePlanner.Plan(session.Base, local, remote, session.Merge).WithDeferred(frozen);

                Result guard = MassDeleteGuard.Check(plan, session.AllowMassDelete);
                if (guard.IsFailure)
                {
                    return Result.Failure<Manifest?>(guard.Error);
                }

                if (session.DryRun)
                {
                    return Result.Success<Manifest?>(null);
                }

                List<(SharePath, FileEntry)> toUpload = [.. plan.Actions
                    .Where(action => action.Kind == SyncActionKind.Upload && !uploadFailures.ContainsKey(action.Path))
                    .Select(action => (action.Path, (FileEntry)action.Local!))];

                UploadOutcome upload = await transfers.UploadAsync(session.ShareId, toUpload, progress, session.Parallelism, ct);
                uploaded = new TransferTotals(uploaded.Files + upload.Uploaded.Files, uploaded.Bytes + upload.Uploaded.Bytes);
                foreach ((SharePath path, SkippedPath failure) in upload.Failed)
                {
                    uploadFailures[path] = failure;
                }

                plan = plan.WithDeferred(uploadFailures.Keys);

                if (!plan.ChangesRemote)
                {
                    return Result.Success<Manifest?>(null);
                }

                progress.PhaseStarted(SyncPhase.Committing);
                Result<Manifest> next = snapshot.Manifest.CreateNextRevision(plan.RemoteResult(), Now(), machine.Name);
                return next.IsSuccess ? next.Value : Result.Failure<Manifest?>(next.Error);
            },
            cancellationToken);

        if (commit.IsFailure)
        {
            return Result.Failure<SyncReport>(commit.Error);
        }

        CommitOutcome outcome = commit.Value;
        Manifest head = outcome.Committed ?? outcome.Remote.Manifest;
        foreach ((SharePath path, SkippedPath failure) in uploadFailures)
        {
            skipped[path.Value] = failure;
        }

        if (outcome.Committed is not null && !outcome.HistoryWritten)
        {
            warnings.Add($"Revision {head.Revision} was committed, but its history copy could not be written.");
        }

        if (session.DryRun)
        {
            return Report(session, outcome.Remote.Manifest.Revision, committed: false, plan, uploaded, TransferTotals.None, 0, skipped, warnings,
                ConflictTracker.Track(plan, session.PreviousConflicts, outcome.Remote.Manifest, Now()).Open);
        }

        // Phase 8: apply remote changes locally.
        ApplyOutcome applied = await applier.ApplyAsync(session.ShareId, plan, scan, head.Entries, progress, session.Parallelism, cancellationToken);
        plan = plan.WithDeferred([.. applied.Deferred.Keys]);
        foreach ((SharePath path, SkippedPath failure) in applied.Deferred)
        {
            skipped[path.Value] = failure;
        }

        foreach (SyncAction action in plan.Skipped.Where(action => action.SkipReason == SkipReason.NoDelete))
        {
            skipped.TryAdd(action.Path.Value, new SkippedPath(action.Path.Value, SkipCause.NoDelete, "kept because of --no-delete"));
        }

        // Phase 9: record conflicts and the new base.
        // The remote side of a conflict is what was planned against, not this run's own commit.
        (ImmutableArray<Conflict> conflicts, IReadOnlyList<Conflict> toLog) = ConflictTracker.Track(plan, session.PreviousConflicts, outcome.Remote.Manifest, Now());
        foreach (Conflict conflict in toLog)
        {
            Result logged = await conflictLog.RecordDetectedAsync(conflict, cancellationToken);
            if (logged.IsFailure)
            {
                logger.LogWarning("Could not write the conflict log: {Error}", logged.Error.Code);
                warnings.Add($"Could not write {StateFileNames.ConflictLog}: {logged.Error.Description}");
            }
        }

        var state = ShareState.Create(
            session.Location, session.ShareId, head.Revision, plan.NewBase(), NewHashCache(scan, applied), conflicts);

        Result saved = await stateStore.SaveAsync(state, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<SyncReport>(saved.Error);
        }

        await CleanStagingAsync(warnings, cancellationToken);

        return Report(session, head.Revision, outcome.Committed is not null, plan, uploaded, applied.Downloaded, applied.DeletedLocally, skipped, warnings, conflicts);
    }

    /// <summary>
    /// Paths the merge must not act on this run: ones the scan skipped (unstable, unreadable,
    /// symlinks) and ones the local ignore rules match. For them the local side is taken to be
    /// the base, so nothing is uploaded or deleted remotely, and their actions are deferred, so
    /// nothing is downloaded over them or deleted locally. The manifest keeps their remote entry.
    /// </summary>
    private static (IReadOnlyDictionary<SharePath, ManifestEntry> Local, HashSet<SharePath> Frozen) FreezeUnscannable(
        IReadOnlyDictionary<SharePath, ManifestEntry> baseEntries,
        ScannedFolder scan,
        IEnumerable<SharePath> scanSkipped,
        ImmutableSortedDictionary<SharePath, ManifestEntry> remote,
        IIgnoreMatcher ignore,
        Dictionary<string, SkippedPath> skipped)
    {
        HashSet<SharePath> frozen = [.. scanSkipped];

        // A skipped directory (unreadable, or a symlink now) hides everything under it: none of
        // that is "deleted here".
        List<SharePath> skippedRoots = [.. scanSkipped];
        if (skippedRoots.Count > 0)
        {
            frozen.UnionWith(baseEntries.Keys.Concat(remote.Keys).Where(path => skippedRoots.Exists(path.IsUnder)));
        }

        foreach (SharePath path in baseEntries.Keys.Concat(remote.Keys).Where(path => IsIgnored(ignore, path, remote, baseEntries)))
        {
            frozen.Add(path);

            if (remote.ContainsKey(path) && !scan.Entries.ContainsKey(path))
            {
                skipped.TryAdd(path.Value, new SkippedPath(path.Value, SkipCause.IgnoredLocally, "the remote has it, but local ignore rules match it"));
            }
        }

        if (frozen.Count == 0)
        {
            return (scan.Entries, frozen);
        }

        var local = scan.Entries.ToBuilder();
        foreach (SharePath path in frozen)
        {
            if (baseEntries.TryGetValue(path, out ManifestEntry? entry))
            {
                local[path] = entry;
            }
            else
            {
                local.Remove(path);
            }
        }

        return (local.ToImmutable(), frozen);
    }

    private static bool IsIgnored(
        IIgnoreMatcher ignore, SharePath path, ImmutableSortedDictionary<SharePath, ManifestEntry> remote, IReadOnlyDictionary<SharePath, ManifestEntry> baseEntries)
    {
        ManifestEntry? entry = remote.GetValueOrDefault(path) ?? baseEntries.GetValueOrDefault(path);

        if (ignore.IsIgnored(path, entry is DirectoryEntry))
        {
            return true;
        }

        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ignore.IsIgnored(ancestor, isDirectory: true))
            {
                return true;
            }
        }

        return false;
    }

    // Scanned files, minus what was deleted, plus what was downloaded.
    private static ImmutableSortedDictionary<SharePath, HashCacheEntry> NewHashCache(ScannedFolder scan, ApplyOutcome applied)
    {
        var cache = scan.HashCache.ToBuilder();
        cache.RemoveRange(applied.DeletedPaths);

        foreach ((SharePath path, HashCacheEntry entry) in applied.CacheUpdates)
        {
            cache[path] = entry;
        }

        return cache.ToImmutable();
    }

    private static SyncReport Report(
        SyncSession session,
        Revision revision,
        bool committed,
        SyncPlan plan,
        TransferTotals uploaded,
        TransferTotals downloaded,
        int deletedLocally,
        Dictionary<string, SkippedPath> skipped,
        List<string> warnings,
        ImmutableArray<Conflict> conflicts) =>
        new(
            session.ShareId,
            revision,
            committed,
            session.DryRun,
            uploaded,
            downloaded,
            deletedLocally,
            committed ? plan.RemoteEntryDeletes : 0,
            conflicts,
            [.. skipped.Values.OrderBy(skip => skip.Path, StringComparer.Ordinal)],
            [.. warnings],
            plan);

    private static SkipCause Map(LocalSkipReason reason) => reason switch
    {
        LocalSkipReason.Unstable => SkipCause.Unstable,
        LocalSkipReason.Unreadable => SkipCause.Unreadable,
        LocalSkipReason.InvalidName => SkipCause.InvalidName,
        _ => SkipCause.Symlink,
    };

    private DateTimeOffset Now() => new(clock.UtcNow, TimeSpan.Zero);

    private async Task CleanStagingAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        Result cleaned = await folder.CleanStagingAsync(cancellationToken);
        if (cleaned.IsFailure)
        {
            warnings.Add($"Could not clean {StateFileNames.StagingDirectory}: {cleaned.Error.Description}");
        }
    }
}

/// <summary>Everything one sync needs from phase 1 and from the command.</summary>
internal sealed record SyncSession(
    ShareId ShareId,
    StorageLocation Location,
    IReadOnlyDictionary<SharePath, ManifestEntry> Base,
    ImmutableSortedDictionary<SharePath, HashCacheEntry> HashCache,
    ImmutableArray<Conflict> PreviousConflicts,
    MergeOptions Merge,
    bool AllowMassDelete,
    bool DryRun,
    ISyncProgressReporter Progress,
    int Parallelism);
