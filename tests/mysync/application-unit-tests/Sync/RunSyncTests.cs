using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.Shares;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// <c>mysync sync</c> in an existing share, between two "machines" sharing one store
/// (CLAUDE.md §9). Every test ends with both folders and the remote where they should be.
/// </summary>
public class RunSyncTests
{
    [Fact]
    public async Task EditsOnBothMachines_Should_ReachBothFolders()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("keep.txt", "k"), ("edit-a.txt", "1"), ("edit-b.txt", "1"), ("gone.txt", "x"));

        a.Folder.WriteFile("edit-a.txt", "2 from a").WriteFile("new-a.txt", "new");
        a.Folder.Remove("gone.txt");
        b.Folder.WriteFile("edit-b.txt", "2 from b").WriteFile("dir/new-b.txt", "new");

        SyncReport fromA = (await a.SyncAsync()).ShouldSucceed();
        SyncReport fromB = (await b.SyncAsync()).ShouldSucceed();
        SyncReport backToA = (await a.SyncAsync()).ShouldSucceed();

        fromA.Revision.ShouldBe(Rev(2));
        fromB.Revision.ShouldBe(Rev(3));
        backToA.Revision.ShouldBe(Rev(3));
        backToA.Committed.ShouldBeFalse();
        a.Files().ShouldBe(b.Files());
        a.Files().ShouldBe(new Dictionary<string, string>
        {
            ["dir/new-b.txt"] = "new",
            ["edit-a.txt"] = "2 from a",
            ["edit-b.txt"] = "2 from b",
            ["keep.txt"] = "k",
            ["new-a.txt"] = "new",
        });
        (await world.HeadAsync(shareId)).Entries.Count.ShouldBe(5);
        fromB.Downloaded.Files.ShouldBe(2);
        fromB.DeletedLocally.ShouldBe(1);
    }

    [Fact]
    public async Task NothingChanged_Should_CommitNothing_AndTransferNothing()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "a"));

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeFalse();
        report.Uploaded.ShouldBe(TransferTotals.None);
        report.Downloaded.ShouldBe(TransferTotals.None);
        report.Conflicts.ShouldBeEmpty();
        report.Skipped.ShouldBeEmpty();
        (await world.HeadAsync(shareId)).Revision.ShouldBe(Revision.Initial);
    }

    [Fact]
    public async Task AnExecutableBitChange_Should_SyncWithoutTransferringBytes()
    {
        (SyncWorld world, Machine a, Machine b, _) = await TwoMachines(("deploy.sh", "#!/bin/sh"));
        int blobsBefore = world.Store.BlobNames.Count(name => name.Contains("/objects/", StringComparison.Ordinal));
        a.Folder.WriteFile("deploy.sh", "#!/bin/sh", executable: true, mtimeUtc: a.Folder.MtimeOf("deploy.sh"));

        SyncReport fromA = (await a.SyncAsync()).ShouldSucceed();
        SyncReport toB = (await b.SyncAsync()).ShouldSucceed();

        fromA.Committed.ShouldBeTrue();
        fromA.Uploaded.Files.ShouldBe(0);
        fromA.Plan.Actions.Single().Kind.ShouldBe(SyncActionKind.UpdateRemoteMode);
        toB.Plan.Actions.Single().Kind.ShouldBe(SyncActionKind.UpdateLocalMode);
        toB.Downloaded.Files.ShouldBe(0);
        b.Folder.IsExecutable("deploy.sh").ShouldBeTrue();
        world.Store.BlobNames.Count(name => name.Contains("/objects/", StringComparison.Ordinal)).ShouldBe(blobsBefore);
    }

    [Fact]
    public async Task AWindowsEditOfALinuxScript_Should_KeepItsExecutableBit()
    {
        SyncWorld world = new();
        Machine linux = world.Machine("linux");
        linux.Folder.WriteFile("deploy.sh", "#!/bin/sh v1", executable: true);
        CreateShareResponse created = (await linux.CreateAsync()).ShouldSucceed();
        Machine windows = world.Machine("windows", new InMemoryLocalFolder(world.Clock) { ExecutableBitSupported = false });
        (await windows.CloneAsync(created.ShareId)).ShouldSucceed();

        windows.Folder.WriteFile("deploy.sh", "#!/bin/sh v2 edited on Windows");
        (await windows.SyncAsync()).ShouldSucceed().Committed.ShouldBeTrue();
        (await linux.SyncAsync()).ShouldSucceed();

        (await world.HeadAsync(created.ShareId)).Entries[ManifestBuilder.PathOf("deploy.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();
        linux.Folder.ReadText("deploy.sh").ShouldBe("#!/bin/sh v2 edited on Windows");
        linux.Folder.IsExecutable("deploy.sh").ShouldBeTrue();
    }

    [Fact]
    public async Task ARemoteDelete_Should_RemoveTheFile_AndDirectoriesItLeavesEmpty()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("docs/deep/only.md", "x"), ("top.txt", "t"), ("logs/keep.log", "k"));
        a.Folder.Remove("docs");
        a.Folder.Remove("logs/keep.log");
        a.Folder.AddDirectory("logs");

        (await a.SyncAsync()).ShouldSucceed();
        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        report.DeletedLocally.ShouldBe(1 + 1);
        b.Folder.Exists("docs").ShouldBeFalse();
        b.Folder.IsDirectory("logs").ShouldBeTrue(); // kept: the manifest now records it as an empty directory
        b.Files().Keys.ShouldBe(["top.txt"]);
    }

    [Fact]
    public async Task NoDelete_Should_KeepFiles_ReportThem_AndDeleteThemOnTheNextNormalSync()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("gone.txt", "x"), ("stays.txt", "s"));
        a.Folder.Remove("gone.txt");
        (await a.SyncAsync()).ShouldSucceed();

        SyncReport kept = (await b.SyncAsync(new RunSyncCommand(NoDelete: true))).ShouldSucceed();

        b.Folder.Exists("gone.txt").ShouldBeTrue();
        kept.Skipped.Single().ShouldBe(new SkippedPath("gone.txt", SkipCause.NoDelete, "kept because of --no-delete"));
        b.State.State!.Base.ContainsKey(ManifestBuilder.PathOf("gone.txt")).ShouldBeTrue();

        (await b.SyncAsync()).ShouldSucceed().DeletedLocally.ShouldBe(1);
        b.Folder.Exists("gone.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task AMassDelete_Should_BeBlockedOnBothMachines_UnlessAllowed()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines([.. Enumerable.Range(0, 12).Select(i => ($"f{i:D2}.txt", $"{i}"))]);
        foreach (int i in Enumerable.Range(0, 12))
        {
            a.Folder.Remove($"f{i:D2}.txt");
        }

        (await a.SyncAsync()).ShouldFail().ShouldBe(SyncErrors.MassDeleteBlocked(12, 12));
        (await world.HeadAsync(shareId)).Entries.Count.ShouldBe(12);

        (await a.SyncAsync(new RunSyncCommand(AllowMassDelete: true))).ShouldSucceed().DeletedRemotely.ShouldBe(12);

        (await b.SyncAsync()).ShouldFail().Code.ShouldBe("Sync.MassDeleteBlocked");
        b.Files().Count.ShouldBe(12);
        (await b.SyncAsync(new RunSyncCommand(AllowMassDelete: true))).ShouldSucceed();
        b.Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task DryRun_Should_PlanWithoutChangingAnything()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "1"));
        a.Folder.WriteFile("a.txt", "2");
        int saves = a.State.Saves;

        SyncReport report = (await a.SyncAsync(new RunSyncCommand(DryRun: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        report.Plan.Actions.Single().Kind.ShouldBe(SyncActionKind.Upload);
        (await world.HeadAsync(shareId)).Revision.ShouldBe(Revision.Initial);
        a.State.Saves.ShouldBe(saves);
        world.Store.BlobNames.ShouldNotContain(name => name.EndsWith(ManifestBuilder.HashOf("2").Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IgnoredPaths_Should_NeitherUploadDownloadNorDelete()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("shared.txt", "s"), ("build/out.bin", "binary"));

        // b starts ignoring "build" after cloning, and has a local ignored file of its own.
        b.IgnoreRules.Matcher = new PathListIgnoreMatcher("build", "local.secret");
        b.Folder.WriteFile("local.secret", "never upload me");
        b.Folder.Remove("build");
        a.Folder.WriteFile("build/out.bin", "rebuilt");
        (await a.SyncAsync()).ShouldSucceed();

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeFalse(); // the locally missing "build" was not taken as a delete
        b.Folder.Exists("build/out.bin").ShouldBeFalse(); // and the new version wasn't downloaded
        report.Skipped.Single().Cause.ShouldBe(SkipCause.IgnoredLocally);
        Manifest head = await world.HeadAsync(shareId);
        head.Entries.Keys.Select(path => path.Value).ShouldBe(["build/out.bin", "shared.txt"]);
    }

    [Fact]
    public async Task CaseOnlyCollisions_Should_NotBeDownloaded_OnACaseInsensitiveFilesystem()
    {
        SyncWorld world = new();
        CreateShareResponse created = (await world.Machine("linux").WriteAndCreate(("Foo.txt", "upper"), ("foo.txt", "lower"), ("ok.txt", "ok"))).ShouldSucceed();
        Machine mac = world.Machine("mac", new InMemoryLocalFolder(world.Clock) { IsCaseSensitive = false });

        SyncReport cloned = (await mac.CloneAsync(created.ShareId)).ShouldSucceed();
        SyncReport again = (await mac.SyncAsync()).ShouldSucceed();

        mac.Files().Keys.ShouldBe(["ok.txt"]);
        cloned.Skipped.Select(skip => (skip.Path, skip.Cause)).ShouldBe([("Foo.txt", SkipCause.CaseCollision), ("foo.txt", SkipCause.CaseCollision)]);
        again.Committed.ShouldBeFalse(); // not downloading them is not a local delete
        (await world.HeadAsync(created.ShareId)).Entries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task WindowsInvalidNames_Should_BeSkippedOnWindows_WithoutFailingTheSync()
    {
        SyncWorld world = new();
        CreateShareResponse created = (await world.Machine("linux").WriteAndCreate(("aux.txt", "device name"), ("what?.txt", "q"), ("fine.txt", "f"))).ShouldSucceed();
        Machine windows = world.Machine("windows", new InMemoryLocalFolder(world.Clock) { RequiresWindowsCompatibleNames = true, ExecutableBitSupported = false });

        SyncReport report = (await windows.CloneAsync(created.ShareId)).ShouldSucceed();

        windows.Files().Keys.ShouldBe(["fine.txt"]);
        report.Skipped.Select(skip => skip.Cause).ShouldBe([SkipCause.WindowsInvalidName, SkipCause.WindowsInvalidName]);
    }

    [Fact]
    public async Task Sync_Should_FailWithNotAShare_InAFolderWithoutState()
    {
        SyncWorld world = new();

        (await world.Machine("a").SyncAsync()).ShouldFail().ShouldBe(ShareErrors.NotAShare());
    }

    [Fact]
    public async Task Sync_Should_FailWithStorageMismatch_WhenTheEnvironmentPointsElsewhere()
    {
        (_, Machine a, _, _) = await TwoMachines(("a.txt", "a"));
        ShareState state = a.State.State!;
        a.State.State = ShareState.Create(
            new StorageLocation("in-memory", "memory", "another-container"), state.ShareId, state.BaseRevision, state.Base, state.HashCache, state.Conflicts);

        (await a.SyncAsync()).ShouldFail().Code.ShouldBe("State.StorageMismatch");
    }

    [Fact]
    public async Task Sync_Should_FailWithNotFound_WhenTheShareIsGoneRemotely()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        var missing = ShareId.New();
        a.State.State = ShareState.Create(InMemoryShareStore.DefaultLocation, missing, Revision.Initial, [], [], []);

        (await a.SyncAsync()).ShouldFail().ShouldBe(ShareErrors.NotFound(missing));
    }

    [Fact]
    public async Task Sync_Should_StopOnADamagedStateFile_WithoutCreatingAShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.State.LoadError = StateErrors.Corrupt("truncated");

        (await a.SyncAsync()).ShouldFail().Code.ShouldBe("State.Corrupt");
        world.Store.BlobNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_Should_FailWithFolderLocked_AndReleaseTheLockAfterwards()
    {
        (_, Machine a, _, _) = await TwoMachines(("a.txt", "a"));

        await using (a.Lock.HoldElsewhere())
        {
            (await a.SyncAsync()).ShouldFail().ShouldBe(StateErrors.FolderLocked);
        }

        (await a.SyncAsync()).ShouldSucceed();
        a.Lock.IsHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task EveryCommit_Should_WriteAHistoryCopy_AndAFailedCopy_Should_OnlyWarn()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "1"));
        a.Folder.WriteFile("a.txt", "2");
        (await a.SyncAsync()).ShouldSucceed();
        world.Store.FailHistoryWrites = true;
        a.Folder.WriteFile("a.txt", "3");

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeTrue();
        report.Warnings.Single().ShouldContain("history copy");
        var layout = Domain.Storage.ShareLayout.For(shareId);
        world.Store.BlobNames.ShouldContain(layout.ManifestHistory(Rev(2)));
        world.Store.BlobNames.ShouldNotContain(layout.ManifestHistory(Rev(3)));
    }

    internal static async Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> TwoMachines(params (string Path, string Content)[] files)
    {
        SyncWorld world = new();
        Machine a = world.Machine("usman@DEV-LAPTOP");
        CreateShareResponse created = (await a.WriteAndCreate(files)).ShouldSucceed();
        Machine b = world.Machine("ali@SERVER01");
        (await b.CloneAsync(created.ShareId)).ShouldSucceed();
        return (world, a, b, created.ShareId);
    }

    internal static Revision Rev(long value) => Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentOutOfRangeException(nameof(value));
}
