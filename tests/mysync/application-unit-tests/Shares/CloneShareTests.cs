using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Shares;

/// <summary><c>mysync sync &lt;id&gt;</c> (CLAUDE.md §9).</summary>
public class CloneShareTests
{
    private static readonly DateTimeOffset OldMtime = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Clone_Should_DownloadEverything_WithExecBitAndMtime_AndWriteTheState()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("docs/readme.md", "# hi", mtimeUtc: OldMtime).WriteFile("run.sh", "#!/bin/sh", executable: true).AddDirectory("logs");
        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();
        Machine b = world.Machine("b");

        SyncReport report = (await b.CloneAsync(created.ShareId)).ShouldSucceed();

        b.Files().ShouldBe(a.Files());
        b.Folder.IsDirectory("logs").ShouldBeTrue();
        b.Folder.IsExecutable("run.sh").ShouldBeTrue();
        b.Folder.MtimeOf("docs/readme.md").ShouldBe(OldMtime);
        report.Downloaded.ShouldBe(new TransferTotals(2, 13));
        report.Committed.ShouldBeFalse();
        report.Revision.ShouldBe(Revision.Initial);
        b.State.State!.Base.ShouldBe((await world.HeadAsync(created.ShareId)).Entries);
        b.State.State.HashCache.Count.ShouldBe(2);
        b.Folder.StagedFileCount.ShouldBe(0);
    }

    [Fact]
    public async Task Clone_Should_Refuse_ANonEmptyFolder()
    {
        SyncWorld world = new();
        CreateShareResponse created = (await world.Machine("a").CreateAsync()).ShouldSucceed();
        Machine b = world.Machine("b");
        b.Folder.WriteFile("mine.txt", "mine");

        (await b.CloneAsync(created.ShareId)).ShouldFail().Code.ShouldBe("Shares.FolderNotEmpty");
        b.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task Clone_Should_IgnoreOsClutter_WhenCheckingTheFolderIsEmpty()
    {
        SyncWorld world = new();
        CreateShareResponse created = (await world.Machine("a").WriteAndCreate("a.txt", "a")).ShouldSucceed();
        Machine b = world.Machine("b");
        b.Folder.WriteFile(".DS_Store", "finder").WriteFile("Thumbs.db", "explorer");
        b.IgnoreRules.Matcher = new PathListIgnoreMatcher(".DS_Store", "Thumbs.db");

        (await b.CloneAsync(created.ShareId)).ShouldSucceed();

        b.Folder.ReadText("a.txt").ShouldBe("a");
    }

    [Fact]
    public async Task Clone_Should_Refuse_WhenTheFolderIsAlreadyAShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        (await a.CloneAsync(ShareId.New())).ShouldFail().ShouldBe(ShareErrors.AlreadyAShare(created.ShareId));
    }

    [Fact]
    public async Task Clone_Should_FailWithNotFound_ForAShareThatDoesNotExist()
    {
        SyncWorld world = new();
        var missing = ShareId.New();

        (await world.Machine("b").CloneAsync(missing)).ShouldFail().ShouldBe(ShareErrors.NotFound(missing));
    }

    [Fact]
    public async Task Clone_Should_TreatSomethingThatIsntAnId_AsAName_AndSayNoShareHasIt()
    {
        SyncWorld world = new();

        Error error = (await world.Machine("b").CloneAsync("not-a-uuid")).ShouldFail();

        error.Code.ShouldBe("Shares.NameNotFound");
        error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task CloneWithForce_Should_MergeLocalFiles_WithoutOverwritingAnything()
    {
        SyncWorld world = new();
        CreateShareResponse created = (await world.Machine("a")
            .WriteAndCreate(("same.txt", "same"), ("different.txt", "theirs"), ("remote-only.txt", "r"))).ShouldSucceed();
        Machine b = world.Machine("b");
        b.Folder.WriteFile("same.txt", "same").WriteFile("different.txt", "mine").WriteFile("local-only.txt", "l");

        SyncReport report = (await b.CloneAsync(created.ShareId, force: true)).ShouldSucceed();

        b.Folder.ReadText("different.txt").ShouldBe("mine");
        b.Folder.ReadText("remote-only.txt").ShouldBe("r");
        report.Conflicts.Single().Kind.ShouldBe(ConflictKind.AddAdd);
        report.Committed.ShouldBeTrue();
        (await world.HeadAsync(created.ShareId)).Entries.Keys.Select(path => path.Value)
            .ShouldBe(["different.txt", "local-only.txt", "remote-only.txt", "same.txt"]);
        b.ConflictLog.Lines.ShouldBe(["CONFLICT add/add different.txt"]);
    }
}

internal static class CreateExtensions
{
    public static Task<Result<CreateShareResponse>> WriteAndCreate(this Machine machine, string path, string content) =>
        machine.WriteAndCreate((path, content));

    public static Task<Result<CreateShareResponse>> WriteAndCreate(this Machine machine, params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            machine.Folder.WriteFile(path, content);
        }

        return machine.CreateAsync();
    }
}
