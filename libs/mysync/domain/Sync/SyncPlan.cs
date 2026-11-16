using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Sync;

/// <summary>
/// The output of <see cref="MergePlanner.Plan"/>: one <see cref="SyncAction"/> per path that exists
/// in the base, the local scan or the remote manifest, sorted ordinally by path.
/// </summary>
public sealed class SyncPlan
{
    internal SyncPlan(ImmutableArray<SyncAction> actions) => Actions = actions;

    public ImmutableArray<SyncAction> Actions { get; }

    public IEnumerable<SyncAction> Conflicts => Actions.Where(action => action.Kind == SyncActionKind.Conflict);

    public IEnumerable<SyncAction> Skipped => Actions.Where(action => action.Kind == SyncActionKind.Skip);

    /// <summary>True if a commit is needed (some local change wins).</summary>
    public bool ChangesRemote => Actions.Any(action => action.IsLocalWins);

    /// <summary>True if something must be applied to the local folder.</summary>
    public bool ChangesLocal => Actions.Any(action => action.IsRemoteWins);

    /// <summary>Files in the local scan.</summary>
    public int LocalFileCount => Actions.Count(action => action.Local is FileEntry);

    /// <summary>Entries in the remote manifest.</summary>
    public int RemoteEntryCount => Actions.Count(action => action.Remote is not null);

    /// <summary>Local files this plan deletes (or replaces with a directory).</summary>
    public int LocalFileDeletes => Actions.Count(action => action.DeletesLocally && action.Local is FileEntry);

    /// <summary>Remote entries this plan removes (or replaces with a directory).</summary>
    public int RemoteEntryDeletes => Actions.Count(action => action.DeletesRemotely);

    /// <summary>
    /// The entries of the manifest to commit: the remote manifest with every local-wins change
    /// applied. Conflicted and skipped paths keep their remote entry.
    /// </summary>
    public ImmutableSortedDictionary<SharePath, ManifestEntry> RemoteResult() => Collect(action => action.RemoteResult);

    /// <summary>What the local folder holds once every remote-wins action is applied.</summary>
    public ImmutableSortedDictionary<SharePath, ManifestEntry> LocalResult() => Collect(action => action.LocalResult);

    /// <summary>
    /// The base to store if every action is applied successfully. Paths whose local apply fails
    /// keep their old base entry instead (CLAUDE.md §9 phase 9); the pipeline handles that.
    /// </summary>
    public ImmutableSortedDictionary<SharePath, ManifestEntry> NewBase() => Collect(action => action.NewBase);

    private ImmutableSortedDictionary<SharePath, ManifestEntry> Collect(Func<SyncAction, ManifestEntry?> select)
    {
        ImmutableSortedDictionary<SharePath, ManifestEntry>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<SharePath, ManifestEntry>(SharePath.OrdinalComparer);

        foreach (SyncAction action in Actions)
        {
            if (select(action) is { } entry)
            {
                builder[action.Path] = entry;
            }
        }

        return builder.ToImmutable();
    }
}
