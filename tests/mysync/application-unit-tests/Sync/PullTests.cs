using System.Text;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Pull;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync.Pull;
using MySync.Testing;
using Shouldly;
using SharedKernel;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// <c>mysync pull</c> (CLAUDE.md §10c): a read-only download of a share, or parts of it, into an
/// ordinary folder. Never writes to the share, never writes sync state, never deletes or silently
/// replaces a local file.
/// </summary>
public class PullTests
{
    private const string Destination = "/home/usman/collect";

    private static readonly DateTimeOffset Old = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ShareReference Team => ShareReference.Parse("team").Value;

    /// <summary>A laptop's share <c>team</c>: a readme, an executable script, a log, an empty folder.</summary>
    private static async Task<(SyncWorld World, Machine Laptop, Machine Collector, ShareId ShareId)> Setup(bool compress = false)
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder
            .WriteFile("docs/readme.md", "# readme", mtimeUtc: Old)
            .WriteFile("scripts/deploy.sh", "#!/bin/sh\necho hi\n", executable: true, mtimeUtc: Old.AddHours(1))
            .WriteFile("logs/app.log", string.Concat(Enumerable.Repeat("GET / 200\n", 200)), mtimeUtc: Old)
            .AddDirectory("empty");
        CreateShareResponse created = (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("team").Value, Compress: compress))).ShouldSucceed();
        return (world, laptop, world.Machine("ali@COLLECTOR"), created.ShareId);
    }

    private static InMemoryLocalFolder Dest(Machine machine, string path = Destination) => machine.Destinations.Folder(path);

    private static Dictionary<string, string> FilesIn(InMemoryLocalFolder folder) =>
        folder.AllPaths.Where(path => !folder.IsDirectory(path)).ToDictionary(path => path, folder.ReadText, StringComparer.Ordinal);

    /// <summary>Two servers push into <c>team</c>: web01 twice, web02 once.</summary>
    private static async Task<(string Web01Old, string Web01New, string Web02)> PushTwoServersAsync(SyncWorld world)
    {
        Machine web01 = world.Machine("svc@web01");
        Machine web02 = world.Machine("svc@web02");
        web01.Sources.WriteFile("/srv/app/a.log", "web01 one");
        web02.Sources.WriteFile("/srv/app/a.log", "web02");

        string first = (await web01.PushAsync(new PushCommand(Team, [], WriteInfo: false))).ShouldSucceed().Target;
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        web01.Sources.WriteFile("/srv/app/a.log", "web01 two");
        string second = (await web01.PushAsync(new PushCommand(Team, [], WriteInfo: false))).ShouldSucceed().Target;
        string other = (await web02.PushAsync(new PushCommand(Team, [], WriteInfo: false))).ShouldSucceed().Target;
        return (first, second, other);
    }

    [Fact]
    public async Task TheWholeShare_Should_LandInTheCurrentFolder_ByteForByte_WithMtimesAndTheExecutableBit()
    {
        (SyncWorld world, _, Machine collector, ShareId shareId) = await Setup();
        Manifest head = await world.HeadAsync(shareId);

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.ShareId.ShouldBe(shareId);
        report.ShareName!.Value.ShouldBe("team");
        report.Revision.ShouldBe(head.Revision);
        report.Destination.ShouldBe(Destination);
        report.Selected.ShouldBe([new PullRoot(string.Empty, string.Empty)]);
        report.Files.ShouldBe(3);
        report.Downloaded.Files.ShouldBe(3);
        report.AlreadyThere.ShouldBe(TransferTotals.None);
        report.Folders.ShouldBe(1);
        report.Skipped.ShouldBeEmpty();
        report.Warnings.ShouldBeEmpty();

        InMemoryLocalFolder dest = Dest(collector);
        FilesIn(dest).Keys.ShouldBe(["docs/readme.md", "logs/app.log", "scripts/deploy.sh"], ignoreOrder: true);
        dest.ReadText("scripts/deploy.sh").ShouldBe("#!/bin/sh\necho hi\n");
        dest.IsExecutable("scripts/deploy.sh").ShouldBeTrue();
        dest.IsExecutable("docs/readme.md").ShouldBeFalse();
        dest.MtimeOf("scripts/deploy.sh").ShouldBe(Old.AddHours(1));
        dest.IsDirectory("empty").ShouldBeTrue();
        dest.StagingCleans.ShouldBe(2); // before and after
    }

    [Fact]
    public async Task AFolder_AFile_AndSeveralPaths_Should_LandUnderTheirNames()
    {
        (_, _, Machine collector, _) = await Setup();

        (await collector.PullAsync(new PullCommand(Team, ["docs"], To: "folder"))).ShouldSucceed();
        (await collector.PullAsync(new PullCommand(Team, ["scripts/deploy.sh"], To: "file"))).ShouldSucceed();
        PullReport several = (await collector.PullAsync(new PullCommand(Team, ["docs", "logs/app.log"], To: "several"))).ShouldSucceed();

        FilesIn(Dest(collector, $"{Destination}/folder")).Keys.ShouldBe(["docs/readme.md"]);
        FilesIn(Dest(collector, $"{Destination}/file")).Keys.ShouldBe(["deploy.sh"]);
        Dest(collector, $"{Destination}/file").IsExecutable("deploy.sh").ShouldBeTrue();
        FilesIn(Dest(collector, $"{Destination}/several")).Keys.ShouldBe(["app.log", "docs/readme.md"], ignoreOrder: true);
        several.Selected.ShouldBe([new PullRoot("docs", "docs"), new PullRoot("logs/app.log", "app.log", IsFile: true)]);
    }

    [Fact]
    public async Task To_Should_UseANewFolder_OrAnExistingOne_TouchingNothingElseThere()
    {
        (_, _, Machine collector, _) = await Setup();
        InMemoryLocalFolder existing = Dest(collector, "/data/existing").WriteFile("mine.txt", "mine").WriteFile("docs/notes.md", "my notes");

        PullReport fresh = (await collector.PullAsync(new PullCommand(Team, ["docs"], To: "/data/new"))).ShouldSucceed();
        (await collector.PullAsync(new PullCommand(Team, ["docs"], To: "/data/existing"))).ShouldSucceed();

        fresh.Destination.ShouldBe("/data/new");
        FilesIn(Dest(collector, "/data/new")).Keys.ShouldBe(["docs/readme.md"]);
        FilesIn(existing).ShouldBe(new Dictionary<string, string>
        {
            ["docs/notes.md"] = "my notes",
            ["docs/readme.md"] = "# readme",
            ["mine.txt"] = "mine",
        });
    }

    [Fact]
    public async Task ACompressedShare_Should_PullTheRealContent()
    {
        (SyncWorld world, Machine laptop, Machine collector, ShareId shareId) = await Setup(compress: true);
        Manifest head = await world.HeadAsync(shareId);
        world.Store.ContentEncodingOf(shareId, ((FileEntry)head.Entries[SharePath.Create("logs/app.log").Value]).Sha256).ShouldBe("gzip");

        (await collector.PullAsync("team", "logs")).ShouldSucceed();

        Dest(collector).ReadBytes("logs/app.log").ShouldBe(laptop.Folder.ReadBytes("logs/app.log"));
    }

    [Fact]
    public async Task AShareWithSyncedFilesAndPushedFolders_Should_PullBoth_AndLatestTakesEachHostsNewest()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();
        (string web01Old, string web01New, string web02) = await PushTwoServersAsync(world);

        PullReport whole = (await collector.PullAsync(new PullCommand(Team, [], To: "all"))).ShouldSucceed();
        PullReport latest = (await collector.PullAsync(new PullCommand(Team, [], To: "latest", Latest: true))).ShouldSucceed();
        PullReport onlyWeb02 = (await collector.PullAsync(new PullCommand(Team, [], To: "web02", Latest: true, Hosts: ["WEB02"]))).ShouldSucceed();

        FilesIn(Dest(collector, $"{Destination}/all")).Keys.ShouldBe(["docs/readme.md", "logs/app.log", "scripts/deploy.sh", $"{web01Old}/a.log", $"{web01New}/a.log", $"{web02}/a.log"], ignoreOrder: true);
        whole.Files.ShouldBe(6);

        latest.Latest.ShouldBeTrue();
        latest.Selected.Select(root => root.SharePath).ShouldBe([web01New, web02]);
        FilesIn(Dest(collector, $"{Destination}/latest")).ShouldBe(new Dictionary<string, string>
        {
            [$"{web01New}/a.log"] = "web01 two",
            [$"{web02}/a.log"] = "web02",
        });
        FilesIn(Dest(collector, $"{Destination}/web02")).Keys.ShouldBe([$"{web02}/a.log"]);
        onlyWeb02.Selected.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Latest_Should_RefuseAMissingHost_AndAShareWithoutPushes()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();

        Error none = (await collector.PullAsync(new PullCommand(Team, [], Latest: true))).ShouldFail();
        await PushTwoServersAsync(world);
        Error missing = (await collector.PullAsync(new PullCommand(Team, [], Latest: true, Hosts: ["db01"]))).ShouldFail();

        none.Code.ShouldBe("Pull.NoPushes");
        none.Description.ShouldBe("team has no pushed folders (<host>/<timestamp>/).");
        missing.Code.ShouldBe("Pull.HostNotFound");
        Dest(collector).AllPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task FilesTheSharesIgnoreRulesMatch_Should_StillBePulled()
    {
        (_, Machine laptop, Machine collector, _) = await Setup();
        laptop.Folder.WriteFile(".mysyncignore", "*.log\n");
        (await laptop.SyncAsync()).ShouldSucceed();

        (await collector.PullAsync("team")).ShouldSucceed();

        FilesIn(Dest(collector)).Keys.ShouldContain(".mysyncignore");
        FilesIn(Dest(collector)).Keys.ShouldContain("logs/app.log");
    }

    [Fact]
    public async Task ARerun_Should_DownloadNothing_FindingEverythingAlreadyThere()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();
        (await collector.PullAsync("team")).ShouldSucceed();
        int reads = world.Store.ObjectReads;

        PullReport again = (await collector.PullAsync("team")).ShouldSucceed();

        again.Downloaded.ShouldBe(TransferTotals.None);
        again.AlreadyThere.Files.ShouldBe(3);
        again.Files.ShouldBe(3);
        world.Store.ObjectReads.ShouldBe(reads);
    }

    [Fact]
    public async Task AnInterruptedPull_Should_FinishOnARerun_DownloadingOnlyWhatWasMissing()
    {
        (SyncWorld world, _, Machine collector, ShareId shareId) = await Setup();
        Manifest head = await world.HeadAsync(shareId);
        Domain.Content.ContentHash log = ((FileEntry)head.Entries[SharePath.Create("logs/app.log").Value]).Sha256;
        world.Store.DownloadStream = (hash, bytes) => hash == log ? new FailingStream(bytes, failAfter: 10) : new MemoryStream(bytes, writable: false);

        PullReport first = (await collector.PullAsync("team")).ShouldSucceed();
        world.Store.DownloadStream = null;
        PullReport second = (await collector.PullAsync("team")).ShouldSucceed();

        first.Skipped.ShouldHaveSingleItem().Path.ShouldBe("logs/app.log");
        first.Skipped[0].Cause.ShouldBe(SkipCause.ApplyFailed);
        first.Downloaded.Files.ShouldBe(2);
        Dest(collector).StagedFileCount.ShouldBe(0); // nothing half-written left behind
        second.Downloaded.Files.ShouldBe(1);
        second.AlreadyThere.Files.ShouldBe(2);
        second.Skipped.ShouldBeEmpty();
        Dest(collector).ReadText("logs/app.log").ShouldStartWith("GET / 200");
    }

    [Fact]
    public async Task ADifferingLocalFile_Should_FailThePull_BeforeAnythingIsDownloaded()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();
        InMemoryLocalFolder dest = Dest(collector).WriteFile("docs/readme.md", "my own readme").WriteFile("other.txt", "untouched");
        int reads = world.Store.ObjectReads;

        Error error = (await collector.PullAsync("team")).ShouldFail();

        error.Code.ShouldBe("Pull.DestinationOccupied");
        error.Description.ShouldContain("'docs/readme.md'");
        error.Description.ShouldContain("--overwrite");
        world.Store.ObjectReads.ShouldBe(reads);
        dest.AllPaths.ShouldBe(["docs", "docs/readme.md", "other.txt"]);
        dest.ReadText("docs/readme.md").ShouldBe("my own readme");
        dest.StagingCleans.ShouldBe(0);
    }

    [Fact]
    public async Task Overwrite_Should_ReplaceTheDifferingFile_AndTouchNothingElse()
    {
        (_, _, Machine collector, _) = await Setup();
        InMemoryLocalFolder dest = Dest(collector).WriteFile("docs/readme.md", "my own readme").WriteFile("other.txt", "untouched");

        PullReport report = (await collector.PullAsync(new PullCommand(Team, [], Overwrite: true))).ShouldSucceed();

        report.Overwritten.ShouldBe(1);
        report.Downloaded.Files.ShouldBe(3);
        dest.ReadText("docs/readme.md").ShouldBe("# readme");
        dest.ReadText("other.txt").ShouldBe("untouched");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFolderWhereAFileGoes_OrAFileWhereAFolderGoes_Should_FailEvenWithOverwrite(bool overwrite)
    {
        (_, _, Machine collector, _) = await Setup();
        InMemoryLocalFolder dest = Dest(collector).AddDirectory("docs/readme.md").WriteFile("scripts", "a file named scripts").WriteFile("empty", "a file");

        Error error = (await collector.PullAsync(new PullCommand(Team, [], Overwrite: overwrite))).ShouldFail();

        error.Code.ShouldBe("Pull.DestinationOccupied");
        error.Description.ShouldStartWith("3 paths at the destination are in the way: 'docs/readme.md', 'empty', 'scripts'.");
        dest.AllPaths.ShouldBe(["docs", "docs/readme.md", "empty", "scripts"]);
    }

    [Theory]
    [InlineData("/home/usman/collect")]
    [InlineData("/home/usman")]
    [InlineData("/home")]
    public async Task ADestinationThatSyncsWithTheShare_Should_BeRefused(string syncedFolder)
    {
        (SyncWorld world, _, Machine collector, ShareId shareId) = await Setup();
        collector.Sources.SyncedWith(syncedFolder, shareId);
        int reads = world.Store.ObjectReads;

        Error error = (await collector.PullAsync(new PullCommand(Team, [], Overwrite: true))).ShouldFail();

        error.Code.ShouldBe("Pull.DestinationIsTheShare");
        error.Description.ShouldContain($"('{syncedFolder}') already syncs with team; use mysync sync.");
        world.Store.ObjectReads.ShouldBe(reads);
        Dest(collector).AllPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADestinationOfAnotherShare_Should_BeAllowed_WithAWarning()
    {
        (_, _, Machine collector, _) = await Setup();
        var other = ShareId.New();
        collector.Sources.SyncedWith("/home/usman", other);

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.Warnings.ShouldBe([$"This folder syncs with {other}; pulled files will be uploaded to it by its next sync."]);
        report.Downloaded.Files.ShouldBe(3);
    }

    [Fact]
    public async Task AMissingAndACorruptObject_Should_BeSkipped_AndTheRestPulled()
    {
        (SyncWorld world, _, Machine collector, ShareId shareId) = await Setup();
        Manifest head = await world.HeadAsync(shareId);
        Domain.Content.ContentHash Hash(string path) => ((FileEntry)head.Entries[SharePath.Create(path).Value]).Sha256;
        (await world.Store.DeleteObjectAsync(shareId, Hash("docs/readme.md"), SyncWorld.Ct)).IsSuccess.ShouldBeTrue();
        world.Store.PutObjectAsStored(shareId, Hash("logs/app.log"), Encoding.UTF8.GetBytes("not the log"), contentEncoding: null);

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.Skipped.Select(skip => (skip.Path, skip.Cause)).ShouldBe([("docs/readme.md", SkipCause.ApplyFailed), ("logs/app.log", SkipCause.ApplyFailed)]);
        report.Files.ShouldBe(1);
        FilesIn(Dest(collector)).Keys.ShouldBe(["scripts/deploy.sh"]);
    }

    [Fact]
    public async Task MysyncsOwnFileNames_Should_NeverBeWritten_EvenIfAManifestHoldsThem()
    {
        (SyncWorld world, _, Machine collector, ShareId shareId) = await Setup();
        ManifestBuilder planted = new ManifestBuilder(shareId)
            .WithFile(".mysynctool", """{"shareId":"planted"}""")
            .WithFile("sub/.mysynctool.lock", "x")
            .WithFile("ok.txt", "fine");
        foreach ((Domain.Content.ContentHash hash, byte[] bytes) in planted.Contents)
        {
            world.Store.PutObjectAsStored(shareId, hash, bytes, contentEncoding: null);
        }

        Abstractions.Storage.ManifestSnapshot head = (await world.Store.GetManifestAsync(shareId, SyncWorld.Ct)).Value!;
        Manifest crafted = head.Manifest.CreateNextRevision(planted.Entries, DateTimeOffset.UtcNow, "mallory@EVIL").Value;
        (await world.Store.ReplaceManifestAsync(shareId, crafted, head.ETag, SyncWorld.Ct)).IsSuccess.ShouldBeTrue();

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.Skipped.Select(skip => (skip.Path, skip.Cause)).ShouldBe([(".mysynctool", SkipCause.InvalidName), ("sub/.mysynctool.lock", SkipCause.InvalidName)]);
        Dest(collector).AllPaths.ShouldBe(["ok.txt"]);
    }

    [Fact]
    public async Task NamesTheDiskCantHold_Should_BeSkipped_NotFatal()
    {
        (_, Machine laptop, Machine collector, _) = await Setup();
        laptop.Folder.WriteFile("Readme.TXT", "upper").WriteFile("readme.txt", "lower").WriteFile("con.txt", "reserved");
        (await laptop.SyncAsync()).ShouldSucceed();
        collector.Destinations.Create = path => new InMemoryLocalFolder { RootPath = path, IsCaseSensitive = false, RequiresWindowsCompatibleNames = true };

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.Skipped.Select(skip => (skip.Path, skip.Cause)).ShouldBe([
            ("Readme.TXT", SkipCause.CaseCollision),
            ("con.txt", SkipCause.WindowsInvalidName),
            ("readme.txt", SkipCause.CaseCollision)]);
        Dest(collector).Exists("docs/readme.md").ShouldBeTrue();
        Dest(collector).Exists("readme.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task AShareChangingDuringThePull_Should_NotChangeWhatIsPulled()
    {
        (SyncWorld world, Machine laptop, Machine collector, ShareId shareId) = await Setup();
        Revision start = (await world.HeadAsync(shareId)).Revision;
        bool changed = false;
        world.Store.DownloadStream = (_, bytes) =>
        {
            if (!changed)
            {
                changed = true;
                laptop.Folder.WriteFile("docs/readme.md", "# edited meanwhile").WriteFile("new.txt", "new");
                laptop.SyncAsync().GetAwaiter().GetResult().ShouldSucceed();
            }

            return new MemoryStream(bytes, writable: false);
        };

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        changed.ShouldBeTrue();
        (await world.HeadAsync(shareId)).Revision.ShouldNotBe(start);
        report.Revision.ShouldBe(start);
        Dest(collector).ReadText("docs/readme.md").ShouldBe("# readme");
        Dest(collector).Exists("new.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task DryRun_Should_WriteNothing_NotEvenTheDestination()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();
        int reads = world.Store.ObjectReads;

        PullReport report = (await collector.PullAsync(new PullCommand(Team, [], To: "/data/new", DryRun: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Downloaded.Files.ShouldBe(3);
        report.Folders.ShouldBe(1);
        InMemoryLocalFolder dest = Dest(collector, "/data/new");
        dest.AllPaths.ShouldBeEmpty();
        dest.StagingCleans.ShouldBe(0);
        world.Store.ObjectReads.ShouldBe(reads);
    }

    [Fact]
    public async Task APull_Should_NeverWriteToTheStore_NorTouchSyncState()
    {
        (SyncWorld world, _, Machine collector, _) = await Setup();
        await PushTwoServersAsync(world);
        int writes = world.Store.Writes;
        IReadOnlyList<string> blobs = world.Store.BlobNames;

        (await collector.PullAsync("team")).ShouldSucceed();
        (await collector.PullAsync(new PullCommand(Team, [], To: "latest", Latest: true))).ShouldSucceed();
        (await collector.PullAsync(new PullCommand(Team, ["docs"], To: "dry", DryRun: true))).ShouldSucceed();

        world.Store.Writes.ShouldBe(writes);
        world.Store.BlobNames.ShouldBe(blobs);
        collector.State.Saves.ShouldBe(0);
        collector.State.State.ShouldBeNull();
        collector.Lock.Acquisitions.ShouldBe(0);
        collector.ConflictLog.Lines.ShouldBeEmpty();
        collector.Folder.AllPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyShare_Should_HaveNothingToPull()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("team").Value))).ShouldSucceed();
        Machine collector = world.Machine("ali@COLLECTOR");

        PullReport report = (await collector.PullAsync("team")).ShouldSucceed();

        report.NothingToPull.ShouldBeTrue();
        Dest(collector).StagingCleans.ShouldBe(0);
    }

    [Fact]
    public async Task UsageErrors_Should_FailInTheValidatorOrBeforeAnyDownload()
    {
        (_, _, Machine collector, _) = await Setup();

        Error latestWithPaths = (await collector.PullAsync(new PullCommand(Team, ["docs"], Latest: true))).ShouldFail();
        Error hostWithoutLatest = (await collector.PullAsync(new PullCommand(Team, [], Hosts: ["web01"]))).ShouldFail();
        Error badPath = (await collector.PullAsync(new PullCommand(Team, ["../etc"]))).ShouldFail();
        Error notThere = (await collector.PullAsync("team", "nope")).ShouldFail();
        Error duplicate = (await collector.PullAsync("team", "docs/readme.md", "docs/readme.md")).ShouldFail();
        Error noShare = (await collector.PullAsync("no-such-share")).ShouldFail();

        latestWithPaths.ShouldBeOfType<ValidationError>().Errors.ShouldHaveSingleItem().Code.ShouldBe("Pull.LatestWithPaths");
        hostWithoutLatest.ShouldBeOfType<ValidationError>().Errors.ShouldHaveSingleItem().Code.ShouldBe("Pull.HostNeedsLatest");
        badPath.ShouldBeOfType<ValidationError>().Type.ShouldBe(ErrorType.Validation);
        notThere.Code.ShouldBe("Pull.PathNotFound");
        duplicate.Code.ShouldBe("Pull.DuplicateTarget");
        noShare.Code.ShouldBe("Shares.NameNotFound");
        Dest(collector).AllPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADeletedShare_Should_FailWithDeleted_ByIdAsWellAsByItsFreedName()
    {
        (_, Machine laptop, Machine collector, ShareId shareId) = await Setup();
        (await laptop.DeleteAsync()).ShouldSucceed();

        (await collector.PullAsync(shareId.ToString())).ShouldFail().Code.ShouldBe("Shares.Deleted");
        (await collector.PullAsync("team")).ShouldFail().Code.ShouldBe("Shares.NameNotFound"); // delete freed the name
        Dest(collector).AllPaths.ShouldBeEmpty();
    }
}
