using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Sync;

/// <summary>Options that change the plan. <c>--dry-run</c> doesn't: it just doesn't apply it.</summary>
/// <param name="NoDelete"><c>--no-delete</c>: every delete becomes a skip that keeps its base entry.</param>
public sealed record MergeOptions(bool NoDelete = false)
{
    public static MergeOptions Default { get; } = new();
}

/// <summary>
/// The three-way merge (CLAUDE.md §9). A pure function of base, local and remote: no I/O, no
/// clock, deterministic, so it can be tested exhaustively.
/// </summary>
/// <remarks>
/// <para>Per path, comparing with <see cref="ManifestEntry.AreEquivalent"/> (type, hash, exec bit),
/// rows evaluated in order:</para>
/// <list type="number">
/// <item><c>L == R</c>: no change; base becomes <c>L</c>.</item>
/// <item><c>L == B</c>, <c>R != B</c>: remote wins.</item>
/// <item><c>R == B</c>, <c>L != B</c>: local wins.</item>
/// <item>otherwise: conflict, dropped and logged. Nothing happens to the path on either side.</item>
/// </list>
/// <para>Then a tree check: per-path decisions can combine into something no filesystem can hold,
/// e.g. local adds a file <c>a</c> while remote adds <c>a/b</c>. Wherever the resulting manifest or the
/// resulting local folder would have an entry inside a file, the paths whose change caused it
/// become <see cref="ConflictKind.Type"/> conflicts (or skips, if a <c>--no-delete</c> skip caused it),
/// repeated until both results are consistent. Each round only turns actions into conflicts or
/// skips, which restore the original (consistent) remote and local entries, so it terminates.</para>
/// </remarks>
public static class MergePlanner
{
    public static SyncPlan Plan(
        IReadOnlyDictionary<SharePath, ManifestEntry> baseEntries,
        IReadOnlyDictionary<SharePath, ManifestEntry> local,
        IReadOnlyDictionary<SharePath, ManifestEntry> remote,
        MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        options ??= MergeOptions.Default;

        SortedDictionary<SharePath, SyncAction> actions = new(SharePath.OrdinalComparer);

        foreach (SharePath path in baseEntries.Keys.Concat(local.Keys).Concat(remote.Keys))
        {
            if (!actions.ContainsKey(path))
            {
                actions[path] = Decide(
                    path,
                    baseEntries.GetValueOrDefault(path),
                    local.GetValueOrDefault(path),
                    remote.GetValueOrDefault(path),
                    options);
            }
        }

        // Repeat until neither side changes: a new conflict can expose another clash.
        bool changed;
        do
        {
            bool remoteChanged = ResolveTreeClashes(actions, action => action.RemoteResult, action => action.Remote);
            bool localChanged = ResolveTreeClashes(actions, action => action.LocalResult, action => action.Local);
            changed = remoteChanged || localChanged;
        }
        while (changed);

        return new SyncPlan([.. actions.Values]);
    }

    /// <summary>The conflict kind for a row-4 path, from what each side did since the base.</summary>
    public static ConflictKind Classify(ManifestEntry? baseEntry, ManifestEntry? local, ManifestEntry? remote)
    {
        bool typeChanged = TypesDiffer(local, remote) || TypesDiffer(baseEntry, local) || TypesDiffer(baseEntry, remote);

        return (baseEntry, local, remote) switch
        {
            _ when typeChanged => ConflictKind.Type,
            (null, _, _) => ConflictKind.AddAdd,
            (_, not null, null) => ConflictKind.ModifyDelete,
            (_, null, not null) => ConflictKind.DeleteModify,
            _ => ConflictKind.ModifyModify,
        };
    }

    private static bool TypesDiffer(ManifestEntry? left, ManifestEntry? right) =>
        left is not null && right is not null && left.Type != right.Type;

    private static SyncAction Decide(SharePath path, ManifestEntry? b, ManifestEntry? l, ManifestEntry? r, MergeOptions options)
    {
        SyncAction action = ApplyRows(path, b, l, r);

        return options.NoDelete && (action.DeletesLocally || action.DeletesRemotely)
            ? action with { Kind = SyncActionKind.Skip, SkipReason = SkipReason.NoDelete }
            : action;
    }

    private static SyncAction ApplyRows(SharePath path, ManifestEntry? b, ManifestEntry? l, ManifestEntry? r)
    {
        if (ManifestEntry.AreEquivalent(l, r))
        {
            return new SyncAction(path, SyncActionKind.NoChange, b, l, r); // row 1
        }

        if (ManifestEntry.AreEquivalent(l, b))
        {
            return new SyncAction(path, RemoteWins(l, r), b, l, r); // row 2
        }

        if (ManifestEntry.AreEquivalent(r, b))
        {
            return new SyncAction(path, LocalWins(l, r), b, l, r); // row 3
        }

        return new SyncAction(path, SyncActionKind.Conflict, b, l, r, Classify(b, l, r)); // row 4
    }

    private static SyncActionKind RemoteWins(ManifestEntry? local, ManifestEntry? remote) => remote switch
    {
        null => SyncActionKind.DeleteLocal,
        DirectoryEntry => SyncActionKind.CreateLocalDirectory,
        FileEntry r when local is FileEntry l && l.Sha256 == r.Sha256 => SyncActionKind.UpdateLocalMode,
        _ => SyncActionKind.Download,
    };

    private static SyncActionKind LocalWins(ManifestEntry? local, ManifestEntry? remote) => local switch
    {
        null => SyncActionKind.RemoveRemote,
        DirectoryEntry => SyncActionKind.AddRemoteDirectory,
        FileEntry l when remote is FileEntry r && l.Sha256 == r.Sha256 => SyncActionKind.UpdateRemoteMode,
        _ => SyncActionKind.Upload,
    };

    /// <summary>
    /// Finds entries that would end up inside a file on one side and turns the actions that
    /// caused it (those whose result differs from that side's original entry) into conflicts.
    /// Returns true if anything changed.
    /// </summary>
    private static bool ResolveTreeClashes(
        SortedDictionary<SharePath, SyncAction> actions,
        Func<SyncAction, ManifestEntry?> result,
        Func<SyncAction, ManifestEntry?> original)
    {
        HashSet<SharePath> files = [.. actions.Values.Where(a => result(a) is FileEntry).Select(a => a.Path)];
        List<(SharePath File, SharePath Inside)> clashes = [];

        foreach (SyncAction action in actions.Values.Where(a => result(a) is not null))
        {
            for (SharePath? ancestor = action.Path.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (files.Contains(ancestor))
                {
                    clashes.Add((ancestor, action.Path));
                }
            }
        }

        bool changed = false;

        foreach ((SharePath file, SharePath inside) in clashes)
        {
            bool causedBySkip = actions[file].Kind == SyncActionKind.Skip || actions[inside].Kind == SyncActionKind.Skip;

            foreach (SharePath path in (ImmutableArray<SharePath>)[file, inside])
            {
                SyncAction action = actions[path];

                if (ManifestEntry.AreEquivalent(result(action), original(action)))
                {
                    continue; // this side's entry is unchanged; it didn't cause the clash
                }

                actions[path] = causedBySkip
                    ? action with { Kind = SyncActionKind.Skip, ConflictKind = null, SkipReason = SkipReason.NoDelete }
                    : action with { Kind = SyncActionKind.Conflict, ConflictKind = ConflictKind.Type, SkipReason = null };
                changed = true;
            }
        }

        return changed;
    }
}
