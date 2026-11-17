using MySync.Application.Conflicts.List;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Sync;

/// <summary>The read-only commands (CLAUDE.md §10): <c>status</c>, <c>info</c>, <c>conflicts</c>.</summary>
public class StatusInfoAndConflictsTests
{
    [Fact]
    public async Task Status_Should_ShowWhatASyncWouldDo_UploadsDownloadsAndConflicts()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("up.txt", "1"), ("down.txt", "1"), ("both.txt", "base"));
        b.Folder.WriteFile("down.txt", "b's edit").WriteFile("both.txt", "b's edit");
        (await b.SyncAsync()).ShouldSucceed();
        a.Folder.WriteFile("up.txt", "a's edit").WriteFile("both.txt", "a's edit");

        SyncReport status = (await a.StatusAsync()).ShouldSucceed();

        status.DryRun.ShouldBeTrue();
        status.Revision.ShouldBe(Rev(2));
        Kinds(status).ShouldBe(new Dictionary<string, SyncActionKind>
        {
            ["both.txt"] = SyncActionKind.Conflict,
            ["down.txt"] = SyncActionKind.Download,
            ["up.txt"] = SyncActionKind.Upload,
        });
        status.Conflicts.Single().Path.Value.ShouldBe("both.txt");
    }

    [Fact]
    public async Task Status_Should_WriteNothing_NoLockStateLogStagingOrRemoteChange()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("x.txt", "base"));
        a.Folder.WriteFile("x.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b's edit").WriteFile("new.txt", "new");
        int saves = b.State.Saves;
        IReadOnlyList<string> blobs = world.Store.BlobNames;
        int cleans = b.Folder.StagingCleans;
        int locks = b.Lock.Acquisitions;

        (await b.StatusAsync()).ShouldSucceed().Conflicts.Length.ShouldBe(1);

        b.State.Saves.ShouldBe(saves);
        b.State.State!.Conflicts.ShouldBeEmpty(); // the conflict is shown, not recorded
        b.Lock.Acquisitions.ShouldBe(locks);
        b.ConflictLog.Lines.ShouldBeEmpty();
        b.Folder.StagingCleans.ShouldBe(cleans);
        world.Store.BlobNames.ShouldBe(blobs);
        (await world.HeadAsync(shareId)).Revision.ShouldBe(Rev(2));
    }

    [Fact]
    public async Task Status_Should_RunWhileAnotherSyncHoldsTheLock()
    {
        (_, Machine a, _, _) = await TwoMachines(("x.txt", "1"));
        await using IAsyncDisposable other = a.Lock.HoldElsewhere();

        (await a.StatusAsync()).ShouldSucceed();
    }

    [Fact]
    public async Task Status_Should_WarnAboutAMassDelete_InsteadOfFailing()
    {
        (_, Machine a, _, _) = await TwoMachines([.. Enumerable.Range(0, 12).Select(i => ($"f{i}.txt", $"{i}"))]);
        foreach (int i in Enumerable.Range(0, 12))
        {
            a.Folder.Remove($"f{i}.txt");
        }

        SyncReport status = (await a.StatusAsync()).ShouldSucceed();

        status.Plan.RemoteEntryDeletes.ShouldBe(12);
        status.Warnings.Single().ShouldContain("A sync would stop here");
    }

    [Fact]
    public async Task Status_Should_ReportARemoteEntryHiddenByLocalIgnoreRules()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("seed.txt", "s"));
        b.Folder.WriteFile("build.log", "log");
        (await b.SyncAsync()).ShouldSucceed();
        a.IgnoreRules.Matcher = new PathListIgnoreMatcher("build.log");

        SyncReport status = (await a.StatusAsync()).ShouldSucceed();

        status.Skipped.Single().ShouldBe(status.Skipped.Single() with { Path = "build.log", Cause = SkipCause.IgnoredLocally });
    }

    [Fact]
    public async Task Status_Info_AndConflicts_Should_RefuseAFolderThatIsNotAShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");

        (await a.StatusAsync()).ShouldFail().ShouldBe(ShareErrors.NotAShare());
        (await a.InfoAsync()).ShouldFail().ShouldBe(ShareErrors.NotAShare());
        (await a.ConflictsAsync()).ShouldFail().ShouldBe(ShareErrors.NotAShare());
    }

    [Fact]
    public async Task Info_Should_CompareThisFolderWithTheRemote()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("a.txt", "12345"), ("b.txt", "123"));
        b.Folder.WriteFile("c.txt", "1");
        (await b.SyncAsync()).ShouldSucceed();

        ShareInfoResponse info = (await a.InfoAsync()).ShouldSucceed();

        info.ShareId.ShouldBe(shareId);
        info.Location.ShouldBe(InMemoryShareStore.DefaultLocation);
        info.BaseRevision.ShouldBe(Rev(1));
        info.RemoteRevision.ShouldBe(Rev(2));
        info.RemoteUpdatedBy.ShouldBe("ali@SERVER01");
        info.FileCount.ShouldBe(3);
        info.TotalBytes.ShouldBe(9);
        info.OpenConflicts.ShouldBe(0);
        info.RootPath.ShouldBe(a.Folder.RootPath);
        world.Store.BlobNames.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Conflicts_Should_ListWhatTheLastSyncRecorded_WithoutTheNetwork()
    {
        (SyncWorld world, Machine a, Machine b, _) = await TwoMachines(("x.txt", "base"), ("y.txt", "base"));
        a.Folder.WriteFile("x.txt", "a").WriteFile("y.txt", "a");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b").WriteFile("y.txt", "b");
        (await b.SyncAsync()).ShouldSucceed();
        world.Store.BeforeListObjects = _ => throw new InvalidOperationException("conflicts must not touch storage");

        ConflictListResponse listed = (await b.ConflictsAsync()).ShouldSucceed();

        listed.Conflicts.Select(conflict => conflict.Path.Value).ShouldBe(["x.txt", "y.txt"]);
        listed.Conflicts.ShouldBe(b.State.State!.Conflicts);
        (await b.InfoAsync()).ShouldSucceed().OpenConflicts.ShouldBe(2);
    }

    private static Dictionary<string, SyncActionKind> Kinds(SyncReport report) =>
        report.Plan.Actions.Where(action => action.Kind != SyncActionKind.NoChange).ToDictionary(action => action.Path.Value, action => action.Kind);
}
