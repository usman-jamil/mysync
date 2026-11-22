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
using MySync.Domain.Content;
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
    IShareStore store,
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
        Result<LocalScan> scanned = await ScanAsync(session, warnings, cancellationToken);
        if (scanned.IsFailure)
        {
            return Result.Failure<SyncReport>(scanned.Error);
        }

        (ScannedFolder scan, Dictionary<string, SkippedPath> skipped) = (scanned.Value.Folder, scanned.Value.Skipped);
        HashSet<SharePath> stillInConflict = [];

        // Phases 3–7: read remote, plan, guard, upload, commit (re-planned if someone else commits first).
        SyncPlan plan = null!;
        Dictionary<SharePath, SkippedPath> uploadFailures = [];
        TransferTotals uploaded = TransferTotals.None;
        HashSet<ContentHash> sent = [];

        Result<CommitOutcome> commit = await committer.CommitAsync(
            session.ShareId,
            async (snapshot, ct) =>
            {
                (plan, stillInConflict) = PlanAgainst(session, scanned.Value, snapshot.Manifest.Entries);

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

                // The share's setting as of this attempt's HEAD: another machine may turn it on mid-run.
                UploadOutcome upload = await transfers.UploadAsync(session.ShareId, toUpload, snapshot.Manifest.Compression, progress, session.Parallelism, ct);
                uploaded = uploaded.Plus(upload.Uploaded);
                sent.UnionWith(upload.Sent);
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
            if (commit.Error.Code == ShareDeletedCode)
            {
                await RemoveSentAsync(session.ShareId, sent, cancellationToken);
            }

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
            ImmutableArray<Conflict> planned = ConflictTracker.Track(plan, session.PreviousConflicts, outcome.Remote.Manifest, Now()).Open;
            if (session.Resolve is { } preview)
            {
                ImmutableArray<SharePath> wouldResolve = [.. preview.Keep.Keys
                    .Where(path => !stillInConflict.Contains(path) && !planned.Any(conflict => conflict.Path == path) && !plan.Skipped.Any(action => action.Path == path))
                    .Order(SharePath.OrdinalComparer)];
                return Report(session, outcome.Remote.Manifest.Revision, committed: false, plan, uploaded, TransferTotals.None, 0,
                    new Dictionary<string, SkippedPath>(skipped.Where(skip => preview.Keep.Keys.Any(path => path.Value == skip.Key)), StringComparer.Ordinal),
                    warnings, [.. planned.Where(conflict => preview.Keep.ContainsKey(conflict.Path))], wouldResolve);
            }

            return Report(session, outcome.Remote.Manifest.Revision, committed: false, plan, uploaded, TransferTotals.None, 0, skipped, warnings, planned, []);
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
        ImmutableArray<SharePath> resolved = [];
        if (session.Resolve is { } resolve)
        {
            // Resolve touches only the named paths: every other open conflict stays exactly as it
            // was recorded (and isn't logged again), whatever this run's plan says about it.
            conflicts = [.. conflicts.Where(conflict => resolve.Keep.ContainsKey(conflict.Path))
                .Concat(session.PreviousConflicts.Where(conflict => !resolve.Keep.ContainsKey(conflict.Path)))
                .OrderBy(conflict => conflict.Path, SharePath.OrdinalComparer)];
            toLog = [.. toLog.Where(conflict => resolve.Keep.ContainsKey(conflict.Path))];
            resolved = [.. resolve.Keep.Keys
                .Where(path => !stillInConflict.Contains(path) && !conflicts.Any(conflict => conflict.Path == path) && !plan.Skipped.Any(action => action.Path == path))
                .Order(SharePath.OrdinalComparer)];
            await LogResolvedAsync(resolved, resolve, warnings, cancellationToken);
        }

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

        if (session.Resolve is { } named)
        {
            // The report is about the named paths; .mysynctool keeps every open conflict.
            Dictionary<string, SkippedPath> namedSkips = new(skipped.Where(skip => named.Keep.Keys.Any(path => path.Value == skip.Key)), StringComparer.Ordinal);
            return Report(session, head.Revision, outcome.Committed is not null, plan, uploaded, applied.Downloaded, applied.DeletedLocally, namedSkips, warnings,
                [.. conflicts.Where(conflict => named.Keep.ContainsKey(conflict.Path))], resolved);
        }

        return Report(session, head.Revision, outcome.Committed is not null, plan, uploaded, applied.Downloaded, applied.DeletedLocally, skipped, warnings, conflicts, resolved);
    }

    /// <summary>
    /// What a sync would do now, without doing any of it (<c>mysync status</c>): no lock, no
    /// staging clean-up, no upload, no commit, no state or log written. A tripped mass-delete
    /// guard is a warning here, since nothing is being deleted.
    /// </summary>
    public async Task<Result<SyncReport>> PreviewAsync(SyncSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<string> warnings = [];

        Result<LocalScan> scanned = await ScanAsync(session, warnings, cancellationToken);
        if (scanned.IsFailure)
        {
            return Result.Failure<SyncReport>(scanned.Error);
        }

        Result<ManifestSnapshot?> remote = await store.GetManifestAsync(session.ShareId, cancellationToken);
        if (remote.IsFailure)
        {
            return Result.Failure<SyncReport>(remote.Error);
        }

        if (remote.Value is not { } head)
        {
            return Result.Failure<SyncReport>(ShareErrors.NotFound(session.ShareId));
        }

        if (head.Manifest.Deletion is { } deletion)
        {
            return Result.Failure<SyncReport>(ShareErrors.Deleted(session.ShareId, deletion));
        }

        (SyncPlan plan, _) = PlanAgainst(session, scanned.Value, head.Manifest.Entries);
        Result guard = MassDeleteGuard.Check(plan, session.AllowMassDelete);
        if (guard.IsFailure)
        {
            warnings.Add($"{guard.Error.Description} A sync would stop here.");
        }

        ImmutableArray<Conflict> conflicts = ConflictTracker.Track(plan, session.PreviousConflicts, head.Manifest, Now()).Open;
        return Report(session with { DryRun = true }, head.Manifest.Revision, committed: false, plan, TransferTotals.None, TransferTotals.None, 0, scanned.Value.Skipped, warnings, conflicts, []);
    }

    private const string ShareDeletedCode = "Shares.Deleted";

    /// <summary>
    /// The share was deleted while this run was uploading: what this run stored would otherwise be
    /// left in the container after <c>mysync delete</c> had swept it. Best effort; nothing else is touched.
    /// </summary>
    private async Task RemoveSentAsync(ShareId shareId, IEnumerable<ContentHash> sent, CancellationToken cancellationToken)
    {
        foreach (ContentHash hash in sent)
        {
            Result removed = await store.DeleteObjectAsync(shareId, hash, cancellationToken);
            if (removed.IsFailure)
            {
                logger.LogWarning("Could not remove an object uploaded to the deleted share: {Error}", removed.Error.Code);
            }
        }
    }

    // Phase 2: ignore rules and the scan, with what was skipped and why. Writes nothing.
    private async Task<Result<LocalScan>> ScanAsync(SyncSession session, List<string> warnings, CancellationToken cancellationToken)
    {
        Result<IIgnoreMatcher> ignore = await ignoreRules.LoadAsync(cancellationToken);
        if (ignore.IsFailure)
        {
            return Result.Failure<LocalScan>(ignore.Error);
        }

        session.Progress.PhaseStarted(SyncPhase.Scanning);
        Result<ScannedFolder> scanned = await scanner.ScanAsync(
            new ScanRequest(ignore.Value, session.HashCache, session.Base), session.Progress, cancellationToken);
        if (scanned.IsFailure)
        {
            return Result.Failure<LocalScan>(scanned.Error);
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

        return new LocalScan(ignore.Value, scan, skipped, scanSkipped);
    }

    /// <summary>
    /// Phase 4 against one remote manifest. For <c>resolve</c>, a named path's base is moved to the
    /// side being given up (the remote for <c>--keep local</c>, the local for <c>--keep remote</c>), so
    /// the ordinary merge rows carry the kept side across; every other path is deferred. A named path
    /// whose remote changed since its conflict was recorded is left a conflict: it is never forced.
    /// </summary>
    private static (SyncPlan Plan, HashSet<SharePath> StillInConflict) PlanAgainst(
        SyncSession session, LocalScan scan, ImmutableSortedDictionary<SharePath, ManifestEntry> remote)
    {
        (IReadOnlyDictionary<SharePath, ManifestEntry> local, HashSet<SharePath> frozen) =
            FreezeUnscannable(session.Base, scan.Folder, scan.ScanSkipped, remote, scan.Ignore, scan.Skipped);

        if (session.Resolve is not { } resolve)
        {
            return (MergePlanner.Plan(session.Base, local, remote, session.Merge).WithDeferred(frozen), []);
        }

        Dictionary<SharePath, ManifestEntry> planBase = new(session.Base);
        HashSet<SharePath> stillInConflict = [];
        foreach ((SharePath path, KeepSide keep) in resolve.Keep)
        {
            Conflict recorded = session.PreviousConflicts.Single(conflict => conflict.Path == path);
            ManifestEntry? remoteNow = remote.GetValueOrDefault(path);
            if (!ManifestEntry.AreEquivalent(remoteNow, recorded.Remote) || frozen.Contains(path))
            {
                stillInConflict.Add(path);
                continue;
            }

            ManifestEntry? givenUp = keep == KeepSide.Local ? remoteNow : local.GetValueOrDefault(path);
            if (givenUp is null)
            {
                planBase.Remove(path);
            }
            else
            {
                planBase[path] = givenUp;
            }
        }

        SyncPlan plan = MergePlanner.Plan(planBase, local, remote, session.Merge);
        HashSet<SharePath> untouched = [.. plan.Actions.Select(action => action.Path).Where(path => !resolve.Keep.ContainsKey(path))];
        untouched.UnionWith(frozen);
        return (plan.WithDeferred(untouched), stillInConflict);
    }

    private async Task LogResolvedAsync(IEnumerable<SharePath> resolved, ResolveRequest resolve, List<string> warnings, CancellationToken cancellationToken)
    {
        foreach (SharePath path in resolved)
        {
            Result logged = await conflictLog.RecordResolvedAsync(path, resolve.Keep[path], Now(), cancellationToken);
            if (logged.IsFailure)
            {
                logger.LogWarning("Could not write the conflict log: {Error}", logged.Error.Code);
                warnings.Add($"Could not write {StateFileNames.ConflictLog}: {logged.Error.Description}");
            }
        }
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
        ImmutableArray<Conflict> conflicts,
        ImmutableArray<SharePath> resolved) =>
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
            plan)
        {
            Resolved = resolved,
        };

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
    int Parallelism,
    ResolveRequest? Resolve = null);

/// <summary><c>mysync resolve</c>: which side to keep for each named conflicted path.</summary>
internal sealed record ResolveRequest(IReadOnlyDictionary<SharePath, KeepSide> Keep);

/// <summary>The scan as the planner needs it.</summary>
internal sealed record LocalScan(IIgnoreMatcher Ignore, ScannedFolder Folder, Dictionary<string, SkippedPath> Skipped, IReadOnlyList<SharePath> ScanSkipped);
