using FluentValidation;
using MySync.Application.Conflicts.Resolve;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Conflicts;

/// <summary><c>mysync resolve</c> (CLAUDE.md §10): the only way a conflict is decided.</summary>
public class ResolveTests
{
    [Fact]
    public async Task KeepLocal_Should_UploadTheLocalVersion_ClearTheConflict_AndLogIt()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await Conflicted("x.txt");

        SyncReport report = (await b.ResolveAsync("local", "x.txt")).ShouldSucceed();

        report.Resolved.Select(path => path.Value).ShouldBe(["x.txt"]);
        report.Conflicts.ShouldBeEmpty();
        report.Committed.ShouldBeTrue();
        (await world.HeadAsync(shareId)).Entries[ManifestBuilder.PathOf("x.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("b's edit"));
        b.State.State!.Conflicts.ShouldBeEmpty();
        b.State.State.Base[ManifestBuilder.PathOf("x.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("b's edit"));
        b.ConflictLog.Lines[^1].ShouldBe("RESOLVED keep-local x.txt");

        (await a.SyncAsync()).ShouldSucceed();
        a.Folder.ReadText("x.txt").ShouldBe("b's edit");
    }

    [Fact]
    public async Task KeepRemote_Should_DownloadTheRemoteVersion_WithoutCommitting()
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Conflicted("x.txt");
        Revision before = (await world.HeadAsync(shareId)).Revision;

        SyncReport report = (await b.ResolveAsync("remote", "x.txt")).ShouldSucceed();

        report.Resolved.Select(path => path.Value).ShouldBe(["x.txt"]);
        report.Committed.ShouldBeFalse();
        report.Downloaded.Files.ShouldBe(1);
        b.Folder.ReadText("x.txt").ShouldBe("a's edit");
        b.State.State!.Conflicts.ShouldBeEmpty();
        b.ConflictLog.Lines[^1].ShouldBe("RESOLVED keep-remote x.txt");
        (await world.HeadAsync(shareId)).Revision.ShouldBe(before);
        (await b.SyncAsync()).ShouldSucceed().Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public async Task KeepLocal_OfALocalDelete_Should_RemoveTheEntryFromTheShare()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("x.txt", "base"));
        a.Folder.WriteFile("x.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.Remove("x.txt");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.DeleteModify);

        (await b.ResolveAsync("local", "x.txt")).ShouldSucceed().Resolved.Length.ShouldBe(1);

        (await world.HeadAsync(shareId)).Entries.ContainsKey(ManifestBuilder.PathOf("x.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task KeepRemote_OfARemoteDelete_Should_DeleteTheLocalFile()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("x.txt", "base"), ("other.txt", "o"));
        a.Folder.Remove("x.txt");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b's edit");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyDelete);

        SyncReport report = (await b.ResolveAsync("remote", "x.txt")).ShouldSucceed();

        report.Resolved.Length.ShouldBe(1);
        b.Folder.Exists("x.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task Resolve_Should_TouchOnlyTheNamedPaths()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("x.txt", "base"), ("y.txt", "base"), ("pending-up.txt", "1"), ("pending-down.txt", "1"));
        a.Folder.WriteFile("x.txt", "a").WriteFile("y.txt", "a").WriteFile("pending-down.txt", "a's change");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x.txt", "b").WriteFile("y.txt", "b");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Length.ShouldBe(2);
        b.Folder.WriteFile("pending-up.txt", "b's unsynced edit");
        // pending-down.txt was already applied by b's sync above; change it again remotely.
        a.Folder.WriteFile("pending-down.txt", "a's second change");
        (await a.SyncAsync()).ShouldSucceed();

        SyncReport report = (await b.ResolveAsync("local", "x.txt")).ShouldSucceed();

        report.Resolved.Select(path => path.Value).ShouldBe(["x.txt"]);
        report.Conflicts.ShouldBeEmpty(); // y.txt is still open, but resolve reports only what it was asked about
        report.Uploaded.Files.ShouldBe(1);
        report.Downloaded.Files.ShouldBe(0);
        b.Folder.ReadText("pending-down.txt").ShouldBe("a's change");
        Manifest head = await world.HeadAsync(shareId);
        head.Entries[ManifestBuilder.PathOf("pending-up.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("1"));
        b.State.State!.Conflicts.Select(conflict => conflict.Path.Value).ShouldBe(["y.txt"]);
        b.ConflictLog.Lines.Count(line => line.StartsWith("RESOLVED", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task All_Should_ResolveEveryOpenConflict()
    {
        (_, _, Machine b, _) = await Conflicted("x.txt", "y.txt", "z.txt");

        SyncReport report = (await b.ResolveAsync(new ResolveConflictsCommand([], All: true, "remote"))).ShouldSucceed();

        report.Resolved.Select(path => path.Value).ShouldBe(["x.txt", "y.txt", "z.txt"]);
        b.State.State!.Conflicts.ShouldBeEmpty();
        b.Folder.ReadText("y.txt").ShouldBe("a's edit");
    }

    [Fact]
    public async Task APathNotInConflict_Should_BeAUsageError_AndChangeNothing()
    {
        (_, _, Machine b, _) = await Conflicted("x.txt");
        int saves = b.State.Saves;

        Error error = (await b.ResolveAsync("local", "x.txt", "not-conflicted.txt")).ShouldFail();

        error.Code.ShouldBe("Sync.NotInConflict");
        error.Type.ShouldBe(ErrorType.Validation);
        error.Description.ShouldContain("not-conflicted.txt");
        b.State.Saves.ShouldBe(saves);
        b.State.State!.Conflicts.Length.ShouldBe(1);
    }

    [Fact]
    public async Task ARemoteThatChangedAgain_Should_StayAConflict_NotBeForced()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await Conflicted("x.txt");
        a.Folder.WriteFile("x.txt", "a's second edit");
        (await a.SyncAsync()).ShouldSucceed();

        SyncReport report = (await b.ResolveAsync("local", "x.txt")).ShouldSucceed();

        report.Resolved.ShouldBeEmpty();
        report.Conflicts.Single().Path.Value.ShouldBe("x.txt");
        report.Committed.ShouldBeFalse();
        (await world.HeadAsync(shareId)).Entries[ManifestBuilder.PathOf("x.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's second edit"));
        b.State.State!.Conflicts.Single().Remote.ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's second edit"));
    }

    [Fact]
    public async Task LosingTheRaceToANewerRemoteChange_Should_ReportTheConflict_AfterReplanning()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await Conflicted("x.txt");
        bool fired = false;
        world.Store.BeforeReplaceManifest = async _ =>
        {
            if (!fired)
            {
                fired = true;
                a.Folder.WriteFile("x.txt", "a's edit during the resolve");
                (await a.SyncAsync()).ShouldSucceed();
            }
        };

        SyncReport report = (await b.ResolveAsync("local", "x.txt")).ShouldSucceed();

        report.Resolved.ShouldBeEmpty();
        report.Conflicts.Single().Path.Value.ShouldBe("x.txt");
        (await world.HeadAsync(shareId)).Entries[ManifestBuilder.PathOf("x.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's edit during the resolve"));
        b.Folder.ReadText("x.txt").ShouldBe("b's edit");
    }

    [Fact]
    public async Task DryRun_Should_SayWhatWouldBeResolved_AndChangeNothing()
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Conflicted("x.txt");
        Revision before = (await world.HeadAsync(shareId)).Revision;
        int saves = b.State.Saves;

        SyncReport report = (await b.ResolveAsync(new ResolveConflictsCommand(["x.txt"], All: false, "local", DryRun: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Resolved.Select(path => path.Value).ShouldBe(["x.txt"]);
        report.Plan.Actions.Single(action => action.Path.Value == "x.txt").Kind.ShouldBe(SyncActionKind.Upload);
        (await world.HeadAsync(shareId)).Revision.ShouldBe(before);
        b.State.Saves.ShouldBe(saves);
        b.State.State!.Conflicts.Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("both", false, new[] { "x.txt" }, "Conflicts.InvalidKeep")]
    [InlineData(null, false, new[] { "x.txt" }, "Conflicts.InvalidKeep")]
    [InlineData("local", false, new string[0], "Conflicts.PathsOrAll")]
    [InlineData("local", true, new[] { "x.txt" }, "Conflicts.PathsOrAll")]
    [InlineData("local", false, new[] { "../escape" }, "Conflicts.InvalidPath")]
    public async Task InvalidArguments_Should_BeRejected_BeforeAnythingRuns(string? keep, bool all, string[] paths, string code)
    {
        (_, _, Machine b, _) = await Conflicted("x.txt");

        Error error = (await b.ResolveAsync(new ResolveConflictsCommand(paths, all, keep))).ShouldFail();

        error.ShouldBeOfType<ValidationError>().Errors.Select(inner => inner.Code).ShouldContain(code);
    }

    [Fact]
    public async Task Resolve_Should_FailWithFolderLocked_WhenAnotherProcessHoldsTheLock()
    {
        (_, _, Machine b, _) = await Conflicted("x.txt");
        await using IAsyncDisposable other = b.Lock.HoldElsewhere();

        (await b.ResolveAsync("local", "x.txt")).ShouldFail().ShouldBe(Domain.State.StateErrors.FolderLocked);
    }

    // Both machines edited each path from "base"; a synced first, so b holds the conflicts.
    private static async Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> Conflicted(params string[] paths)
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines([.. paths.Select(path => (path, "base"))]);
        foreach (string path in paths)
        {
            a.Folder.WriteFile(path, "a's edit");
            b.Folder.WriteFile(path, "b's edit");
        }

        (await a.SyncAsync()).ShouldSucceed();
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Length.ShouldBe(paths.Length);
        return (world, a, b, shareId);
    }
}
