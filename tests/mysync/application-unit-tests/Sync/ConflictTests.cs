using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// Conflicts are dropped and logged, never resolved (CLAUDE.md §9): local bytes, the remote entry
/// and the base stay as they were; the conflict is recorded in the state and logged once; it
/// clears itself when the sides agree again or the local file goes back to its base.
/// </summary>
public class ConflictTests
{
    [Fact]
    public async Task BothSidesEditing_Should_LeaveEverythingAlone_RecordIt_AndLogItOnce()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("report.xlsx", "base"), ("other.txt", "o"));
        a.Folder.WriteFile("report.xlsx", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("report.xlsx", "b's edit").WriteFile("other.txt", "b changed this too");

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        Conflict conflict = report.Conflicts.Single();
        conflict.Path.Value.ShouldBe("report.xlsx");
        conflict.Kind.ShouldBe(ConflictKind.ModifyModify);
        conflict.RemoteUpdatedBy.ShouldBe("usman@DEV-LAPTOP");
        conflict.RemoteRevision.ShouldBe(Rev(2));

        b.Folder.ReadText("report.xlsx").ShouldBe("b's edit");
        Manifest head = await world.HeadAsync(shareId);
        head.Entries[ManifestBuilder.PathOf("report.xlsx")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's edit"));
        b.State.State!.Base[ManifestBuilder.PathOf("report.xlsx")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("base"));
        b.State.State.Conflicts.Single().ShouldBe(conflict);
        b.ConflictLog.Lines.ShouldBe(["CONFLICT modify/modify report.xlsx"]);

        // The rest of the run went ahead.
        report.Committed.ShouldBeTrue();
        head.Entries[ManifestBuilder.PathOf("other.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("b changed this too"));
    }

    [Fact]
    public async Task AnUnchangedConflict_Should_NotBeLoggedAgain_AndKeepItsFirstDetectedTime()
    {
        (SyncWorld world, Machine a, Machine b, _) = await Conflicted();
        DateTimeOffset firstSeen = b.State.State!.Conflicts.Single().FirstDetectedUtc;
        world.Clock.Advance(TimeSpan.FromHours(1));

        SyncReport again = (await b.SyncAsync()).ShouldSucceed();

        again.Conflicts.Single().FirstDetectedUtc.ShouldBe(firstSeen);
        again.Conflicts.Single().LastSeenUtc.ShouldBe(firstSeen.AddHours(1));
        b.ConflictLog.Lines.Count.ShouldBe(1);
        again.Committed.ShouldBeFalse();
        a.Folder.ReadText("x.txt").ShouldBe("a's edit");
    }

    [Fact]
    public async Task AConflictWhoseLocalSideChanges_Should_BeLoggedAgain()
    {
        (_, _, Machine b, _) = await Conflicted();
        b.Folder.WriteFile("x.txt", "b's second edit");

        (await b.SyncAsync()).ShouldSucceed().Conflicts.Length.ShouldBe(1);

        b.ConflictLog.Lines.ShouldBe(["CONFLICT modify/modify x.txt", "CONFLICT modify/modify x.txt"]);
    }

    [Fact]
    public async Task MakingBothSidesIdentical_Should_ClearTheConflict()
    {
        (_, _, Machine b, _) = await Conflicted();
        b.Folder.WriteFile("x.txt", "a's edit");

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        report.Conflicts.ShouldBeEmpty();
        report.Committed.ShouldBeFalse();
        b.State.State!.Conflicts.ShouldBeEmpty();
        b.State.State.Base[ManifestBuilder.PathOf("x.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's edit"));
    }

    [Fact]
    public async Task PuttingTheLocalFileBackToItsBase_Should_ClearTheConflict_AndDownloadTheRemoteVersion()
    {
        (_, _, Machine b, _) = await Conflicted();
        b.Folder.WriteFile("x.txt", "base");

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        report.Conflicts.ShouldBeEmpty();
        report.Downloaded.Files.ShouldBe(1);
        b.Folder.ReadText("x.txt").ShouldBe("a's edit");
    }

    [Fact]
    public async Task BothAddingTheSamePathDifferently_Should_BeAnAddAddConflict()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("seed.txt", "s"));
        a.Folder.WriteFile("notes.md", "a's notes");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("notes.md", "b's notes");

        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.AddAdd);

        b.Folder.ReadText("notes.md").ShouldBe("b's notes");
    }

    [Fact]
    public async Task EditingAFileTheOtherSideDeleted_Should_BeAModifyDeleteConflict_AndKeepTheFile()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("x.txt", "base"));
        a.Folder.Remove("x.txt");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b's edit");

        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyDelete);

        b.Folder.ReadText("x.txt").ShouldBe("b's edit");
    }

    [Fact]
    public async Task DeletingAFileTheOtherSideEdited_Should_BeADeleteModifyConflict_AndNotRestoreIt()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("x.txt", "base"));
        a.Folder.WriteFile("x.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.Remove("x.txt");

        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.DeleteModify);

        b.Folder.Exists("x.txt").ShouldBeFalse();
        (await world.HeadAsync(shareId)).Entries.ContainsKey(ManifestBuilder.PathOf("x.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task OnlyTheMachineThatSyncsSecond_Should_SeeTheConflict()
    {
        (_, Machine a, _, _) = await Conflicted();

        (await a.SyncAsync()).ShouldSucceed().Conflicts.ShouldBeEmpty();

        a.ConflictLog.Lines.ShouldBeEmpty();
    }

    // a and b both edited x.txt from "base"; a synced first, so b holds the conflict.
    private static async Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> Conflicted()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("x.txt", "base"));
        a.Folder.WriteFile("x.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b's edit");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Length.ShouldBe(1);
        return (world, a, b, shareId);
    }
}
