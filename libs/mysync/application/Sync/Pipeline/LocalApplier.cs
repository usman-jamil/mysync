using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>
/// Phase 8 (CLAUDE.md §9): applies the remote-wins actions to the local folder. Deletes first
/// (so a file can become a directory and back), then downloads, directory creations and mode
/// changes in parallel. Before touching a file it re-checks that it still matches the scan; a
/// path that changed, failed, or can't exist here is deferred and keeps its old base entry.
/// </summary>
internal sealed class LocalApplier(ILocalFolder folder, ObjectTransfers transfers)
{
    public async Task<ApplyOutcome> ApplyAsync(
        ShareId shareId,
        SyncPlan plan,
        ScannedFolder scan,
        ImmutableSortedDictionary<SharePath, ManifestEntry> remoteEntries,
        ISyncProgressReporter progress,
        int parallelism,
        CancellationToken cancellationToken)
    {
        ApplyState state = new(scan);
        List<SyncAction> actions = [.. plan.Actions.Where(action => action.IsRemoteWins)];

        DeferUnrepresentable(actions, remoteEntries, state);
        await DeleteAsync(actions, remoteEntries, state, cancellationToken);
        await CreateAndUpdateAsync(shareId, actions, state, progress, parallelism, cancellationToken);

        return new ApplyOutcome(
            new TransferTotals(state.DownloadedFiles, state.DownloadedBytes),
            state.Deleted.Count,
            state.Deferred,
            state.CacheUpdates,
            [.. state.Deleted]);
    }

    // Names this filesystem can't hold: case-only collisions on a case-insensitive filesystem,
    // and Windows-invalid names on Windows. Neither is created; both are reported.
    private void DeferUnrepresentable(List<SyncAction> actions, ImmutableSortedDictionary<SharePath, ManifestEntry> remoteEntries, ApplyState state)
    {
        IReadOnlyList<CaseCollision> collisions = folder.IsCaseSensitive ? [] : PathRules.FindCaseCollisions(remoteEntries.Keys);

        foreach (SyncAction action in actions.Where(action => action.Kind is SyncActionKind.Download or SyncActionKind.CreateLocalDirectory))
        {
            if (collisions.FirstOrDefault(collision => collision.Affects(action.Path)) is { } collision)
            {
                state.Defer(action.Path, SkipCause.CaseCollision, $"differs only in case from {string.Join(", ", collision.Spellings.Select(PathErrors.Display))}");
            }
            else if (folder.RequiresWindowsCompatibleNames && PathRules.CheckWindowsCompatible(action.Path) is { IsFailure: true } windows)
            {
                state.Defer(action.Path, SkipCause.WindowsInvalidName, windows.Error.Description);
            }
        }
    }

    private async Task DeleteAsync(
        List<SyncAction> actions, ImmutableSortedDictionary<SharePath, ManifestEntry> remoteEntries, ApplyState state, CancellationToken cancellationToken)
    {
        // Deepest first, so a directory's contents go before the directory.
        IEnumerable<SyncAction> deletes = actions
            .Where(action => action.DeletesLocally && !state.IsDeferred(action.Path))
            .OrderByDescending(action => action.Path.Value.Length);

        foreach (SyncAction action in deletes)
        {
            if (action.Local is FileEntry)
            {
                if (!await StillAsScannedAsync(action.Path, state, cancellationToken))
                {
                    continue;
                }

                Result deleted = await folder.DeleteFileAsync(action.Path, cancellationToken);
                if (deleted.IsFailure)
                {
                    state.Defer(action.Path, SkipCause.ApplyFailed, deleted.Error.Description);
                    continue;
                }
            }
            else
            {
                Result<bool> removed = await folder.DeleteDirectoryIfEmptyAsync(action.Path, cancellationToken);
                if (removed.IsFailure || !removed.Value)
                {
                    // Something appeared in it (or it is unreadable). Keep the base so the next run tries again.
                    state.Defer(action.Path, SkipCause.ChangedSinceScan, "the directory is not empty");
                    continue;
                }
            }

            if (action.Kind == SyncActionKind.DeleteLocal)
            {
                state.Deleted.Add(action.Path);
            }
        }

        // Directories emptied by those deletes go too, unless the manifest keeps them as empty directories.
        foreach (SharePath deleted in state.Deleted.ToList())
        {
            for (SharePath? parent = deleted.Parent; parent is not null; parent = parent.Parent)
            {
                if (remoteEntries.TryGetValue(parent, out ManifestEntry? entry) && entry is DirectoryEntry)
                {
                    break;
                }

                Result<bool> removed = await folder.DeleteDirectoryIfEmptyAsync(parent, cancellationToken);
                if (removed.IsFailure || !removed.Value)
                {
                    break;
                }
            }
        }
    }

    private async Task CreateAndUpdateAsync(
        ShareId shareId, List<SyncAction> actions, ApplyState state, ISyncProgressReporter progress, int parallelism, CancellationToken cancellationToken)
    {
        List<SyncAction> work = [.. actions.Where(action => action.Kind != SyncActionKind.DeleteLocal && !state.IsDeferred(action.Path))];
        List<SyncAction> downloads = [.. work.Where(action => action.Kind == SyncActionKind.Download)];

        if (work.Count == 0)
        {
            return;
        }

        progress.PhaseStarted(SyncPhase.Downloading);
        if (downloads.Count > 0)
        {
            progress.TransfersPlanned(TransferDirection.Download, downloads.Count, downloads.Sum(action => ((FileEntry)action.Remote!).Size));
        }

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (action, ct) =>
            {
                switch (action.Kind)
                {
                    case SyncActionKind.Download:
                        await DownloadAsync(shareId, action, state, progress, ct);
                        break;
                    case SyncActionKind.CreateLocalDirectory:
                        Result created = await folder.CreateDirectoryAsync(action.Path, ct);
                        if (created.IsFailure)
                        {
                            state.Defer(action.Path, SkipCause.ApplyFailed, created.Error.Description);
                        }

                        break;
                    case SyncActionKind.UpdateLocalMode:
                        if (await StillAsScannedAsync(action.Path, state, ct))
                        {
                            Result mode = await folder.SetExecutableAsync(action.Path, ((FileEntry)action.Remote!).Executable, ct);
                            if (mode.IsFailure)
                            {
                                state.Defer(action.Path, SkipCause.ApplyFailed, mode.Error.Description);
                            }
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected remote-wins action {action.Kind}.");
                }
            });
    }

    private async Task DownloadAsync(ShareId shareId, SyncAction action, ApplyState state, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        var remote = (FileEntry)action.Remote!;

        if (!await StillAsScannedAsync(action.Path, state, cancellationToken))
        {
            return;
        }

        Result<IStagedFile> staged = await transfers.DownloadToStagingAsync(shareId, action.Path, remote, progress, cancellationToken);
        if (staged.IsFailure)
        {
            state.Defer(action.Path, SkipCause.ApplyFailed, staged.Error.Description);
            return;
        }

        await using IStagedFile file = staged.Value;

        // Again right before replacing: the download may have taken a while.
        if (!await StillAsScannedAsync(action.Path, state, cancellationToken))
        {
            return;
        }

        Result placed = await folder.PlaceStagedFileAsync(file, action.Path, remote.MtimeUtc, remote.Executable, cancellationToken);
        if (placed.IsFailure)
        {
            state.Defer(action.Path, SkipCause.ApplyFailed, placed.Error.Description);
            return;
        }

        progress.FileTransferred(TransferDirection.Download, action.Path);
        Result<LocalFile?> stat = await folder.GetFileAsync(action.Path, cancellationToken);
        state.Downloaded(action.Path, remote, stat.IsSuccess ? stat.Value : null);
    }

    /// <summary>
    /// True if the local path still looks as the scan saw it (same size and mtime, or still
    /// absent). Otherwise the path is deferred: someone is editing it, and the next run sees it.
    /// </summary>
    private async Task<bool> StillAsScannedAsync(SharePath path, ApplyState state, CancellationToken cancellationToken)
    {
        LocalFile? expected = state.Scan.Files.GetValueOrDefault(path);
        Result<LocalFile?> current = await folder.GetFileAsync(path, cancellationToken);

        bool same = current.IsSuccess && (current.Value, expected) switch
        {
            (null, null) => true,
            ({ } now, { } then) => now.Size == then.Size && now.MtimeUtc == then.MtimeUtc,
            _ => false,
        };

        if (!same)
        {
            state.Defer(path, SkipCause.ChangedSinceScan, "it changed after the scan");
        }

        return same;
    }

    private sealed class ApplyState(ScannedFolder scan)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<SharePath, SkippedPath> _deferred = [];
        private readonly Dictionary<SharePath, HashCacheEntry> _cache = [];

        public ScannedFolder Scan => scan;

        public List<SharePath> Deleted { get; } = [];

        public int DownloadedFiles { get; private set; }

        public long DownloadedBytes { get; private set; }

        public IReadOnlyDictionary<SharePath, SkippedPath> Deferred
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<SharePath, SkippedPath>(_deferred);
                }
            }
        }

        public IReadOnlyDictionary<SharePath, HashCacheEntry> CacheUpdates
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<SharePath, HashCacheEntry>(_cache);
                }
            }
        }

        public bool IsDeferred(SharePath path)
        {
            lock (_gate)
            {
                return _deferred.ContainsKey(path);
            }
        }

        public void Defer(SharePath path, SkipCause cause, string detail)
        {
            lock (_gate)
            {
                _deferred.TryAdd(path, new SkippedPath(path.Value, cause, detail));
            }
        }

        public void Downloaded(SharePath path, FileEntry remote, LocalFile? stat)
        {
            lock (_gate)
            {
                DownloadedFiles++;
                DownloadedBytes += remote.Size;

                // After a download the file's mtime is the manifest's, so it is not "racy".
                if (stat is not null)
                {
                    _cache[path] = new HashCacheEntry(stat.Size, stat.MtimeUtc.UtcTicks, remote.Sha256);
                }
            }
        }
    }
}

/// <param name="Deferred">Paths not applied this run, with why. Their base entry stays as it was.</param>
/// <param name="CacheUpdates">Hash-cache entries for downloaded files.</param>
/// <param name="DeletedPaths">Files and directories deleted locally (dropped from the hash cache).</param>
internal sealed record ApplyOutcome(
    TransferTotals Downloaded,
    int DeletedLocally,
    IReadOnlyDictionary<SharePath, SkippedPath> Deferred,
    IReadOnlyDictionary<SharePath, HashCacheEntry> CacheUpdates,
    IReadOnlyList<SharePath> DeletedPaths);
