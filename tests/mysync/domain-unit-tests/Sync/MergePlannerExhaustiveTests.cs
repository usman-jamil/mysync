using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Sync;

/// <summary>
/// Generated inputs, checked against properties that must hold for every plan.
/// <list type="bullet">
/// <item>One path: every combination of base, local and remote over {absent, file v1, file v1 with
/// exec bit, file v2, directory} (5³ = 125), against the decision table.</item>
/// <item>A chain <c>a</c>, <c>a/b</c>, <c>a/b/c</c>: every valid tree for each side (so files and
/// directories replace each other across levels), with and without --no-delete.</item>
/// </list>
/// </summary>
public class MergePlannerExhaustiveTests
{
    private static readonly ManifestEntry?[] Values =
    [
        null,
        File(HashOfTest),
        File(HashOfTest, executable: true),
        File(HashOfEmpty, size: 0),
        DirectoryEntry.Instance,
    ];

    private static readonly string[] Chain = ["a", "a/b", "a/b/c"];

    [Fact]
    public void EverySinglePathCombination_Should_FollowTheDecisionTable()
    {
        int combinations = 0;

        foreach (ManifestEntry? b in Values)
        {
            foreach (ManifestEntry? l in Values)
            {
                foreach (ManifestEntry? r in Values)
                {
                    combinations++;
                    SyncAction action = MergePlanner.Plan(Single(b), Single(l), Single(r)).Actions.SingleOrDefault()
                        ?? new SyncAction(Path("x"), SyncActionKind.NoChange, null, null, null); // all absent: no path at all
                    string because = $"B={Show(b)} L={Show(l)} R={Show(r)}";

                    ExpectedRow(b, l, r).ShouldBe(RowOf(action), because);
                    CheckActionInvariants(action, noDelete: false, because);
                }
            }
        }

        combinations.ShouldBe(125);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryChainCombination_Should_ProduceConsistentTreesAndNeverLoseAChange(bool noDelete)
    {
        List<Dictionary<SharePath, ManifestEntry>> trees = [.. ValidChainTrees()];
        MergeOptions options = new(noDelete);
        int plans = 0;

        foreach (Dictionary<SharePath, ManifestEntry> b in trees)
        {
            foreach (Dictionary<SharePath, ManifestEntry> l in trees)
            {
                foreach (Dictionary<SharePath, ManifestEntry> r in trees)
                {
                    plans++;
                    string because = $"B={Show(b)} L={Show(l)} R={Show(r)} noDelete={noDelete}";
                    SyncPlan plan = MergePlanner.Plan(b, l, r, options);

                    foreach (SyncAction action in plan.Actions)
                    {
                        CheckActionInvariants(action, noDelete, because);
                    }

                    // Both results must be trees a filesystem can hold, and a valid manifest.
                    NothingInsideAFile(plan.RemoteResult()).ShouldBeTrue("remote result: " + because);
                    NothingInsideAFile(plan.LocalResult()).ShouldBeTrue("local result: " + because);
                    Manifest.CreateNew(Share(), plan.RemoteResult(), Created, "x").IsSuccess.ShouldBeTrue(because);

                    // Applying the plan and planning again must change nothing more: a second
                    // sync right after a successful one transfers nothing.
                    SyncPlan again = MergePlanner.Plan(plan.NewBase(), plan.LocalResult(), plan.RemoteResult(), options);
                    again.Actions.Where(action => action.IsLocalWins || action.IsRemoteWins)
                        .ShouldBeEmpty("re-plan: " + because);
                }
            }
        }

        plans.ShouldBe(trees.Count * trees.Count * trees.Count);
        trees.Count.ShouldBe(10); // all absent, or exactly one of 3 levels holding one of 3 values
    }

    [Fact]
    public void Plan_Should_BeDeterministic_AndIndependentOfInputOrder()
    {
        Dictionary<SharePath, ManifestEntry> forward = new()
        {
            [Path("a")] = File(HashOfTest),
            [Path("b/c")] = File(HashOfEmpty, 0),
            [Path("d")] = DirectoryEntry.Instance,
        };
        var backward = forward.Reverse().ToDictionary();

        SyncPlan first = MergePlanner.Plan(forward, backward, new Dictionary<SharePath, ManifestEntry>());
        SyncPlan second = MergePlanner.Plan(backward, forward, new Dictionary<SharePath, ManifestEntry>());

        first.Actions.ShouldBe(second.Actions);
    }

    /// <summary>The properties every single action satisfies, whatever the inputs.</summary>
    private static void CheckActionInvariants(SyncAction action, bool noDelete, string because)
    {
        ManifestEntry? b = action.Base;
        ManifestEntry? l = action.Local;
        ManifestEntry? r = action.Remote;

        // A local change is never overwritten or deleted locally; a remote change is never
        // dropped from the manifest. This is "never auto-resolve" stated as a property.
        if (!ManifestEntry.AreEquivalent(l, b))
        {
            ManifestEntry.AreEquivalent(action.LocalResult, l).ShouldBeTrue("local change lost: " + because);
        }

        if (!ManifestEntry.AreEquivalent(r, b))
        {
            ManifestEntry.AreEquivalent(action.RemoteResult, r).ShouldBeTrue("remote change lost: " + because);
        }

        switch (action.Kind)
        {
            case SyncActionKind.NoChange:
                ManifestEntry.AreEquivalent(l, r).ShouldBeTrue(because);
                action.NewBase.ShouldBe(l, because);
                break;
            case SyncActionKind.Conflict or SyncActionKind.Skip:
                action.LocalResult.ShouldBe(l, because);
                action.RemoteResult.ShouldBe(r, because);
                action.NewBase.ShouldBe(b, because);
                (action.Kind == SyncActionKind.Conflict ? action.ConflictKind : action.SkipReason as object).ShouldNotBeNull(because);
                if (action.Kind == SyncActionKind.Skip)
                {
                    noDelete.ShouldBeTrue("skips only come from --no-delete: " + because);
                }

                break;
            case var _ when action.IsRemoteWins:
                ManifestEntry.AreEquivalent(l, b).ShouldBeTrue(because);
                action.LocalResult.ShouldBe(r, because);
                action.RemoteResult.ShouldBe(r, because);
                action.NewBase.ShouldBe(r, because);
                break;
            default:
                action.IsLocalWins.ShouldBeTrue(because);
                ManifestEntry.AreEquivalent(r, b).ShouldBeTrue(because);
                action.LocalResult.ShouldBe(l, because);
                action.RemoteResult.ShouldBe(l, because);
                action.NewBase.ShouldBe(l, because);
                break;
        }

        if (noDelete)
        {
            (action.DeletesLocally || action.DeletesRemotely).ShouldBeFalse("--no-delete deleted: " + because);
        }
    }

    // The decision table, written out independently of the planner.
    private static int ExpectedRow(ManifestEntry? b, ManifestEntry? l, ManifestEntry? r)
    {
        if (Same(l, r))
        {
            return 1;
        }

        if (Same(l, b))
        {
            return 2;
        }

        return Same(r, b) ? 3 : 4;
    }

    private static bool Same(ManifestEntry? x, ManifestEntry? y) => (x, y) switch
    {
        (null, null) => true,
        (FileEntry fx, FileEntry fy) => fx.Sha256.Value == fy.Sha256.Value && fx.Executable == fy.Executable,
        (DirectoryEntry, DirectoryEntry) => true,
        _ => false,
    };

    private static int RowOf(SyncAction action) => action.Kind switch
    {
        SyncActionKind.NoChange => 1,
        SyncActionKind.Conflict => 4,
        _ when action.IsRemoteWins => 2,
        _ => 3,
    };

    /// <summary>Every assignment of {absent, v1, v2, dir} to the chain where nothing is inside a file or a directory entry.</summary>
    private static IEnumerable<Dictionary<SharePath, ManifestEntry>> ValidChainTrees()
    {
        ManifestEntry?[] values = [null, File(HashOfTest), File(HashOfEmpty, size: 0), DirectoryEntry.Instance];

        foreach (ManifestEntry? a in values)
        {
            foreach (ManifestEntry? ab in values)
            {
                foreach (ManifestEntry? abc in values)
                {
                    ManifestEntry?[] levels = [a, ab, abc];
                    int firstPresent = Array.FindIndex(levels, entry => entry is not null);

                    // Only one level may hold an entry: anything below it would be inside it.
                    if (firstPresent >= 0 && levels.Skip(firstPresent + 1).Any(entry => entry is not null))
                    {
                        continue;
                    }

                    yield return Enumerable.Range(0, Chain.Length)
                        .Where(i => levels[i] is not null)
                        .ToDictionary(i => Path(Chain[i]), i => levels[i]!);
                }
            }
        }
    }

    private static bool NothingInsideAFile(ImmutableSortedDictionary<SharePath, ManifestEntry> tree) =>
        tree.Keys.All(path =>
        {
            for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (tree.TryGetValue(ancestor, out ManifestEntry? entry) && entry is FileEntry)
                {
                    return false;
                }
            }

            return true;
        });

    private static Dictionary<SharePath, ManifestEntry> Single(ManifestEntry? entry) =>
        entry is null ? [] : new() { [Path("x")] = entry };

    private static string Show(ManifestEntry? entry) => entry switch
    {
        null => "∅",
        DirectoryEntry => "dir",
        FileEntry file => file.Sha256.Value[..4] + (file.Executable ? "+x" : ""),
        _ => "?",
    };

    private static string Show(Dictionary<SharePath, ManifestEntry> tree) =>
        "{" + string.Join(",", tree.OrderBy(pair => pair.Key, SharePath.OrdinalComparer).Select(pair => $"{pair.Key}:{Show(pair.Value)}")) + "}";
}
