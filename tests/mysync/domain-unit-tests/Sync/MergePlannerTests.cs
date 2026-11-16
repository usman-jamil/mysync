using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Sync;

/// <summary>
/// Named examples of every row of the decision table (CLAUDE.md §9), every action kind, every
/// conflict kind, the cross-path file/directory clashes and --no-delete. The exhaustive
/// properties are in <see cref="MergePlannerExhaustiveTests"/>.
/// </summary>
public class MergePlannerTests
{
    private static readonly FileEntry V1 = File(HashOfTest);
    private static readonly FileEntry V1Exec = File(HashOfTest, executable: true);
    private static readonly FileEntry V2 = File(HashOfEmpty, size: 0);
    private static readonly DirectoryEntry Dir = DirectoryEntry.Instance;

    // Row 1: L == R

    [Fact]
    public void Row1_Unchanged_Should_DoNothing()
    {
        SyncAction action = PlanOne(b: V1, l: V1, r: V1);

        action.Kind.ShouldBe(SyncActionKind.NoChange);
        action.NewBase.ShouldBe(V1);
    }

    [Fact]
    public void Row1_SameChangeOnBothSides_Should_DoNothing_AndMoveTheBase()
    {
        SyncAction action = PlanOne(b: V1, l: V2, r: V2);

        action.Kind.ShouldBe(SyncActionKind.NoChange);
        action.NewBase.ShouldBe(V2);
    }

    [Fact]
    public void Row1_DeletedOnBothSides_Should_DoNothing_AndDropTheBase()
    {
        SyncAction action = PlanOne(b: V1, l: null, r: null);

        action.Kind.ShouldBe(SyncActionKind.NoChange);
        action.NewBase.ShouldBeNull();
    }

    [Fact]
    public void Row1_OnlyTheMtimeDiffers_Should_DoNothing()
    {
        FileEntry touched = Ok(FileEntry.Create(V1.Sha256, V1.Size, Updated, V1.Executable));

        PlanOne(b: V1, l: touched, r: V1).Kind.ShouldBe(SyncActionKind.NoChange);
    }

    // Row 2: L == B, R != B → remote wins

    [Fact]
    public void Row2_RemoteModified_Should_Download()
    {
        PlanOne(b: V1, l: V1, r: V2).Kind.ShouldBe(SyncActionKind.Download);
    }

    [Fact]
    public void Row2_RemoteAdded_Should_Download()
    {
        PlanOne(b: null, l: null, r: V1).Kind.ShouldBe(SyncActionKind.Download);
    }

    [Fact]
    public void Row2_RemoteDeleted_Should_DeleteLocally()
    {
        PlanOne(b: V1, l: V1, r: null).Kind.ShouldBe(SyncActionKind.DeleteLocal);
    }

    [Fact]
    public void Row2_RemoteOnlyChangedTheExecutableBit_Should_UpdateTheModeWithoutDownloading()
    {
        PlanOne(b: V1, l: V1, r: V1Exec).Kind.ShouldBe(SyncActionKind.UpdateLocalMode);
    }

    [Fact]
    public void Row2_RemoteAddedAnEmptyDirectory_Should_CreateItLocally()
    {
        PlanOne(b: null, l: null, r: Dir).Kind.ShouldBe(SyncActionKind.CreateLocalDirectory);
    }

    [Fact]
    public void Row2_RemoteReplacedAFileWithAnEmptyDirectory_Should_CreateTheDirectory_AndCountAsALocalDelete()
    {
        SyncAction action = PlanOne(b: V1, l: V1, r: Dir);

        action.Kind.ShouldBe(SyncActionKind.CreateLocalDirectory);
        action.DeletesLocally.ShouldBeTrue();
    }

    // Row 3: R == B, L != B → local wins

    [Fact]
    public void Row3_LocalModified_Should_Upload()
    {
        PlanOne(b: V1, l: V2, r: V1).Kind.ShouldBe(SyncActionKind.Upload);
    }

    [Fact]
    public void Row3_LocalAdded_Should_Upload()
    {
        PlanOne(b: null, l: V1, r: null).Kind.ShouldBe(SyncActionKind.Upload);
    }

    [Fact]
    public void Row3_LocalDeleted_Should_RemoveTheRemoteEntry()
    {
        PlanOne(b: V1, l: null, r: V1).Kind.ShouldBe(SyncActionKind.RemoveRemote);
    }

    [Fact]
    public void Row3_LocalOnlyChangedTheExecutableBit_Should_UpdateTheManifestWithoutUploading()
    {
        SyncAction action = PlanOne(b: V1, l: V1Exec, r: V1);

        action.Kind.ShouldBe(SyncActionKind.UpdateRemoteMode);
        action.RemoteResult.ShouldBe(V1Exec);
    }

    [Fact]
    public void Row3_LocalAddedAnEmptyDirectory_Should_RecordIt()
    {
        PlanOne(b: null, l: Dir, r: null).Kind.ShouldBe(SyncActionKind.AddRemoteDirectory);
    }

    // Row 4: both changed differently → conflict, dropped and logged

    [Theory]
    [MemberData(nameof(ConflictCases))]
    public void Row4_Should_ClassifyTheConflict_AndLeaveEverySideAsItWas(
        string description, ManifestEntry? b, ManifestEntry? l, ManifestEntry? r, ConflictKind expected)
    {
        SyncAction action = PlanOne(b, l, r);

        action.Kind.ShouldBe(SyncActionKind.Conflict, description);
        action.ConflictKind.ShouldBe(expected, description);
        action.LocalResult.ShouldBe(l);
        action.RemoteResult.ShouldBe(r);
        action.NewBase.ShouldBe(b);
    }

    public static TheoryData<string, ManifestEntry?, ManifestEntry?, ManifestEntry?, ConflictKind> ConflictCases => new()
    {
        { "both modified differently", V1, V2, V1Exec, ConflictKind.ModifyModify },
        { "exec bit on one side, content on the other", V1, V1Exec, V2, ConflictKind.ModifyModify },
        { "both added different files", null, V1, V2, ConflictKind.AddAdd },
        { "local modified, remote deleted", V1, V2, null, ConflictKind.ModifyDelete },
        { "local deleted, remote modified", V1, null, V2, ConflictKind.DeleteModify },
        { "local added a file, remote an empty directory", null, V1, Dir, ConflictKind.Type },
        { "local made the file a directory, remote modified it", V1, Dir, V2, ConflictKind.Type },
        { "remote made the file a directory, local modified it", V1, V2, Dir, ConflictKind.Type },
        { "local made the file a directory, remote deleted it", V1, Dir, null, ConflictKind.Type },
    };

    // Cross-path file/directory clashes

    [Fact]
    public void LocalAddsAFile_WhereRemoteAddsAFileInsideIt_Should_ConflictOnBoth()
    {
        SyncPlan plan = Plan(baseTree: [], local: [("a", V1)], remote: [("a/b", V2)]);

        Kind(plan, "a").ShouldBe(SyncActionKind.Conflict);
        Kind(plan, "a/b").ShouldBe(SyncActionKind.Conflict);
        plan.Conflicts.ShouldAllBe(action => action.ConflictKind == ConflictKind.Type);
    }

    [Fact]
    public void LocalAddsAFile_WhereRemoteAddsADeepPathInsideIt_Should_Conflict()
    {
        SyncPlan plan = Plan(baseTree: [], local: [("a", V1)], remote: [("a/b/c/d", V2)]);

        plan.Conflicts.Select(action => action.Path.Value).ShouldBe(["a", "a/b/c/d"]);
    }

    [Fact]
    public void LocalReplacesADirectoryWithAFile_WhileRemoteIsUnchanged_Should_UploadAndRemove_WithoutConflict()
    {
        SyncPlan plan = Plan(baseTree: [("a/b", V1)], local: [("a", V2)], remote: [("a/b", V1)]);

        Kind(plan, "a").ShouldBe(SyncActionKind.Upload);
        Kind(plan, "a/b").ShouldBe(SyncActionKind.RemoveRemote);
        plan.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void RemoteReplacesADirectoryWithAFile_WhileLocalIsUnchanged_Should_DeleteAndDownload_WithoutConflict()
    {
        SyncPlan plan = Plan(baseTree: [("a/b", V1)], local: [("a/b", V1)], remote: [("a", V2)]);

        Kind(plan, "a/b").ShouldBe(SyncActionKind.DeleteLocal);
        Kind(plan, "a").ShouldBe(SyncActionKind.Download);
    }

    [Fact]
    public void LocalReplacesADirectoryWithAFile_WhileRemoteEditsInsideIt_Should_ConflictOnBoth()
    {
        SyncPlan plan = Plan(baseTree: [("a/b", V1)], local: [("a", V2)], remote: [("a/b", V1Exec)]);

        SyncAction inside = Action(plan, "a/b");
        inside.Kind.ShouldBe(SyncActionKind.Conflict);
        inside.ConflictKind.ShouldBe(ConflictKind.DeleteModify);

        SyncAction file = Action(plan, "a");
        file.Kind.ShouldBe(SyncActionKind.Conflict);
        file.ConflictKind.ShouldBe(ConflictKind.Type);
    }

    [Fact]
    public void AnEmptyDirectoryThatGainsAFile_Should_DropTheDirectoryEntry_AndUploadTheFile()
    {
        SyncPlan plan = Plan(baseTree: [("logs", Dir)], local: [("logs/today.log", V1)], remote: [("logs", Dir)]);

        Kind(plan, "logs").ShouldBe(SyncActionKind.RemoveRemote);
        Kind(plan, "logs/today.log").ShouldBe(SyncActionKind.Upload);
        plan.RemoteResult().Keys.Select(path => path.Value).ShouldBe(["logs/today.log"]);
    }

    [Fact]
    public void UnrelatedPaths_Should_BeDecidedIndependently()
    {
        SyncPlan plan = Plan(
            baseTree: [("same", V1), ("down", V1), ("up", V1), ("both", V1)],
            local: [("same", V1), ("down", V1), ("up", V2), ("both", V2)],
            remote: [("same", V1), ("down", V2), ("up", V1), ("both", V1Exec)]);

        plan.Actions.Select(action => (action.Path.Value, action.Kind)).ShouldBe([
            ("both", SyncActionKind.Conflict),
            ("down", SyncActionKind.Download),
            ("same", SyncActionKind.NoChange),
            ("up", SyncActionKind.Upload),
        ]);
    }

    // --no-delete

    [Fact]
    public void NoDelete_Should_SkipLocalDeletes_AndKeepTheBase()
    {
        SyncAction action = PlanOne(b: V1, l: V1, r: null, new MergeOptions(NoDelete: true));

        action.Kind.ShouldBe(SyncActionKind.Skip);
        action.SkipReason.ShouldBe(SkipReason.NoDelete);
        action.NewBase.ShouldBe(V1);
        action.LocalResult.ShouldBe(V1);
    }

    [Fact]
    public void NoDelete_Should_SkipRemoteDeletes_AndKeepTheBase()
    {
        SyncAction action = PlanOne(b: V1, l: null, r: V1, new MergeOptions(NoDelete: true));

        action.Kind.ShouldBe(SyncActionKind.Skip);
        action.RemoteResult.ShouldBe(V1);
        action.NewBase.ShouldBe(V1);
    }

    [Fact]
    public void NoDelete_Should_SkipReplacingAFileWithADirectory()
    {
        PlanOne(b: V1, l: V1, r: Dir, new MergeOptions(NoDelete: true)).Kind.ShouldBe(SyncActionKind.Skip);
    }

    [Fact]
    public void NoDelete_Should_AlsoSkipWhatDependsOnASkippedDelete_InsteadOfReportingAConflict()
    {
        // Remote replaced file "a" with "a/b". Keeping "a" (no delete) means "a/b" can't be downloaded.
        SyncPlan plan = Plan(baseTree: [("a", V1)], local: [("a", V1)], remote: [("a/b", V2)], new MergeOptions(NoDelete: true));

        Kind(plan, "a").ShouldBe(SyncActionKind.Skip);
        Kind(plan, "a/b").ShouldBe(SyncActionKind.Skip);
        plan.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void NoDelete_Should_LeaveDownloadsAndUploadsAlone()
    {
        SyncPlan plan = Plan(
            baseTree: [("a", V1), ("b", V1)],
            local: [("a", V1), ("b", V2)],
            remote: [("a", V2), ("b", V1)],
            new MergeOptions(NoDelete: true));

        Kind(plan, "a").ShouldBe(SyncActionKind.Download);
        Kind(plan, "b").ShouldBe(SyncActionKind.Upload);
    }

    // Plan-level results

    [Fact]
    public void RemoteResult_Should_BeTheRemoteManifestWithLocalWinsApplied_AndConflictsUntouched()
    {
        SyncPlan plan = Plan(
            baseTree: [("up", V1), ("gone", V1), ("conflict", V1)],
            local: [("up", V2), ("conflict", V2), ("new", V1)],
            remote: [("up", V1), ("gone", V1), ("conflict", V1Exec)]);

        ImmutableSortedDictionary<SharePath, ManifestEntry> result = plan.RemoteResult();

        result.Keys.Select(path => path.Value).ShouldBe(["conflict", "new", "up"]);
        result[Path("up")].ShouldBe(V2);
        result[Path("conflict")].ShouldBe(V1Exec);
        plan.ChangesRemote.ShouldBeTrue();
        plan.ChangesLocal.ShouldBeFalse();
    }

    [Fact]
    public void NewBase_Should_KeepTheOldEntryForConflicts()
    {
        SyncPlan plan = Plan(
            baseTree: [("conflict", V1), ("down", V1)],
            local: [("conflict", V2), ("down", V1)],
            remote: [("conflict", V1Exec), ("down", V2)]);

        ImmutableSortedDictionary<SharePath, ManifestEntry> newBase = plan.NewBase();

        newBase[Path("conflict")].ShouldBe(V1);
        newBase[Path("down")].ShouldBe(V2);
    }

    [Fact]
    public void Classify_Should_BeAvailableForResolveAndReporting()
    {
        MergePlanner.Classify(V1, V2, null).ShouldBe(ConflictKind.ModifyDelete);
    }

    private static SyncAction PlanOne(ManifestEntry? b, ManifestEntry? l, ManifestEntry? r, MergeOptions? options = null)
    {
        static (string, ManifestEntry)[] One(ManifestEntry? entry) => entry is null ? [] : [("x", entry)];

        return Plan(One(b), One(l), One(r), options).Actions.Single();
    }

    private static SyncPlan Plan(
        (string Path, ManifestEntry Entry)[] baseTree,
        (string Path, ManifestEntry Entry)[] local,
        (string Path, ManifestEntry Entry)[] remote,
        MergeOptions? options = null) =>
        MergePlanner.Plan(Tree(baseTree), Tree(local), Tree(remote), options);

    private static Dictionary<SharePath, ManifestEntry> Tree((string Path, ManifestEntry Entry)[] entries) =>
        entries.ToDictionary(entry => Path(entry.Path), entry => entry.Entry);

    private static SyncAction Action(SyncPlan plan, string path) => plan.Actions.Single(action => action.Path.Value == path);

    private static SyncActionKind Kind(SyncPlan plan, string path) => Action(plan, path).Kind;
}
