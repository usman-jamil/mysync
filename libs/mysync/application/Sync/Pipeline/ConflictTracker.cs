using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;

namespace MySync.Application.Sync.Pipeline;

/// <summary>
/// Turns a plan's conflicts into the <c>conflicts</c> list of <c>.mysynctool</c> (CLAUDE.md §9),
/// and decides which ones to write to <c>.mysynctool.log</c>: only those first detected now, or
/// whose local or remote side changed since they were recorded. An unchanged conflict keeps its
/// first-detected time and remote details, and is not logged again.
/// </summary>
internal static class ConflictTracker
{
    public static (ImmutableArray<Conflict> Open, IReadOnlyList<Conflict> ToLog) Track(
        SyncPlan plan, IEnumerable<Conflict> previous, Manifest remote, DateTimeOffset nowUtc)
    {
        var known = previous.ToDictionary(conflict => conflict.Path);
        List<Conflict> open = [];
        List<Conflict> toLog = [];

        foreach (SyncAction action in plan.Conflicts)
        {
            ConflictKind kind = action.ConflictKind ?? MergePlanner.Classify(action.Base, action.Local, action.Remote);
            bool unchanged = known.TryGetValue(action.Path, out Conflict? before)
                && before.Kind == kind
                && ManifestEntry.AreEquivalent(before.Local, action.Local)
                && ManifestEntry.AreEquivalent(before.Remote, action.Remote);

            Conflict conflict = unchanged
                ? before! with { Base = action.Base, Local = action.Local, Remote = action.Remote, LastSeenUtc = nowUtc }
                : new Conflict(action.Path, kind, action.Base, action.Local, action.Remote, remote.Revision, remote.UpdatedBy, nowUtc, nowUtc);

            open.Add(conflict);

            if (!unchanged)
            {
                toLog.Add(conflict);
            }
        }

        return ([.. open], toLog);
    }
}
