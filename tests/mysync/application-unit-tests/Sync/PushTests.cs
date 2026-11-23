using System.Collections.Immutable;
using System.Text;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Domain.Sync.Push;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// <c>mysync push</c> (CLAUDE.md §10b): add-only uploads into <c>&lt;host&gt;/&lt;UTC start&gt;/</c> of an
/// existing share, from busy servers, without any local sync state.
/// </summary>
public class PushTests
{
    // SyncWorld's clock: 2026-11-16 09:00:00 UTC.
    private const string Stamp = "20261116-090000Z";
    private const string Target = "web01/" + Stamp;

    private static readonly SharePath Readme = SharePath.Create("docs/readme.md").Value;

    /// <summary>A laptop with a named share (<c>logs</c>) holding one synced file, and a server that pushes into it.</summary>
    private static async Task<(SyncWorld World, Machine Laptop, Machine Server, ShareId ShareId)> LaptopAndServer(string serverName = "svc@web01")
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder.WriteFile("docs/readme.md", "hello");
        CreateShareResponse created = (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("logs").Value))).ShouldSucceed();
        return (world, laptop, Server(world, serverName), created.ShareId);
    }

    private static Machine Server(SyncWorld world, string name)
    {
        Machine server = world.Machine(name);
        server.Sources.WriteFile("/srv/app/a.log", "a1\n").WriteFile("/srv/app/sub/b.log", "b1\n").AddFolder("/srv/app/empty");
        return server;
    }

    private static string Text(Manifest manifest, SyncWorld world, ShareId shareId, string path)
    {
        var file = (FileEntry)manifest.Entries[SharePath.Create(path).Value];
        return Encoding.UTF8.GetString(world.Store.ReadObject(shareId, file.Sha256));
    }

    private static IEnumerable<string> Paths(Manifest manifest) => manifest.Entries.Keys.Select(path => path.Value);

    [Fact]
    public async Task PushingTheCurrentFolder_Should_AddItsContentsUnderHostAndStartTime()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        Manifest before = await world.HeadAsync(shareId);

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.ShareId.ShouldBe(shareId);
        report.ShareName!.Value.ShouldBe("logs");
        report.Target.ShouldBe(Target);
        report.Committed.ShouldBeTrue();
        report.Created.ShouldBeFalse();
        report.Revision!.Value.ShouldBe(2);
        report.CommitAttempts.ShouldBe(1);
        report.Files.ShouldBe(2);
        report.Bytes.ShouldBe(6);
        report.Uploaded.Files.ShouldBe(2);
        report.AlreadyStored.ShouldBe(TransferTotals.None);
        report.Skipped.ShouldBeEmpty();
        report.Warnings.ShouldBeEmpty();

        Manifest head = await world.HeadAsync(shareId);
        Paths(head).ShouldBe([
            "docs/readme.md",
            $"{Target}/_mysync-push.json",
            $"{Target}/a.log",
            $"{Target}/empty",
            $"{Target}/sub/b.log"]);
        head.Entries[Readme].ShouldBe(before.Entries[Readme]);
        head.Entries[SharePath.Create($"{Target}/empty").Value].ShouldBe(DirectoryEntry.Instance);
        Text(head, world, shareId, $"{Target}/sub/b.log").ShouldBe("b1\n");
        head.UpdatedBy.ShouldBe("svc@web01");
    }

    [Fact]
    public async Task FilesAndFoldersAsArguments_Should_LandUnderTheirOwnNames_FromAnywhere()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources
            .WriteFile("/srv/app/error.log", "e")
            .WriteFile("/var/log/app/x.log", "x")
            .WriteFile("/var/log/app/deep/y.log", "y")
            .WriteFile("/etc/app/config.yml", "c", executable: true);

        PushReport report = (await server.PushAsync("logs", "error.log", "/var/log/app", "../../etc/app/config.yml")).ShouldSucceed();

        report.Files.ShouldBe(4);
        Manifest head = await world.HeadAsync(shareId);
        Paths(head).Where(path => path.StartsWith(Target, StringComparison.Ordinal)).ShouldBe([
            $"{Target}/_mysync-push.json",
            $"{Target}/app/deep/y.log",
            $"{Target}/app/x.log",
            $"{Target}/config.yml",
            $"{Target}/error.log"]);
        ((FileEntry)head.Entries[SharePath.Create($"{Target}/config.yml").Value]).Executable.ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondPush_Should_LandInANewFolder_AndTwoInOneSecondGetADashTwo()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();

        PushReport first = (await server.PushAsync("logs")).ShouldSucceed();
        PushReport sameSecond = (await server.PushAsync("logs")).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        PushReport later = (await server.PushAsync("logs")).ShouldSucceed();

        first.Target.ShouldBe(Target);
        sameSecond.Target.ShouldBe($"{Target}-2");
        later.Target.ShouldBe("web01/20261116-090500Z");
        Manifest head = await world.HeadAsync(shareId);
        head.Revision.Value.ShouldBe(4);
        foreach (string folder in new[] { first.Target, sameSecond.Target, later.Target })
        {
            Text(head, world, shareId, $"{folder}/a.log").ShouldBe("a1\n");
        }
    }

    [Fact]
    public async Task HostOption_Should_NameTheHostFolder_Sanitised()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Host: "db:01."))).ShouldSucceed();

        report.Target.ShouldBe($"db-01/{Stamp}");
        (await world.HeadAsync(shareId)).Entries.ContainsKey(SharePath.Create($"db-01/{Stamp}/a.log").Value).ShouldBeTrue();
    }

    [Fact]
    public async Task ManyConcurrentPushes_Should_AllLand_WithSequentialRevisions_AndNothingElseChanged()
    {
        (SyncWorld world, _, _, ShareId shareId) = await LaptopAndServer();
        world.Options = world.Options with { MaxParallelism = 4 };
        Manifest before = await world.HeadAsync(shareId);
        Machine[] servers = [.. Enumerable.Range(1, 10).Select(i =>
        {
            Machine server = world.Machine($"svc@web{i:D2}");
            server.Sources.WriteFile("/srv/app/a.log", $"from {i}\n").WriteFile("/srv/app/shared.log", "same everywhere\n");
            return server;
        })];

        PushReport[] reports = await Task.WhenAll(servers.Select(async server => (await server.PushAsync("logs")).ShouldSucceed()));

        reports.Select(report => report.Revision!.Value).Order().ShouldBe([.. Enumerable.Range(2, 10).Select(i => (long)i)]);
        Manifest head = await world.HeadAsync(shareId);
        head.Revision.Value.ShouldBe(11);
        head.Entries[Readme].ShouldBe(before.Entries[Readme]);
        for (int i = 1; i <= 10; i++)
        {
            Text(head, world, shareId, $"web{i:D2}/{Stamp}/a.log").ShouldBe($"from {i}\n");
            Text(head, world, shareId, $"web{i:D2}/{Stamp}/shared.log").ShouldBe("same everywhere\n");
        }

        head.Entries.Count.ShouldBe(1 + 10 * 3);
    }

    [Fact]
    public async Task ManyConcurrentPushesFromOneHost_Should_EachGetTheirOwnFolder()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();

        PushReport[] reports = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => (await server.PushAsync("logs")).ShouldSucceed()));

        reports.Select(report => report.Target).Order(StringComparer.Ordinal).ShouldBe(
            [Target, $"{Target}-2", $"{Target}-3", $"{Target}-4", $"{Target}-5", $"{Target}-6"]);
        (await world.HeadAsync(shareId)).Revision.Value.ShouldBe(7);
    }

    [Fact]
    public async Task APushRacingATwoWaySync_Should_KeepBothChanges()
    {
        (SyncWorld world, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        laptop.Folder.WriteFile("docs/readme.md", "edited on the laptop");
        bool fired = false;
        world.Store.BeforeReplaceManifest = async _ =>
        {
            if (!fired)
            {
                fired = true;
                (await laptop.SyncAsync()).ShouldSucceed();
            }
        };

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.CommitAttempts.ShouldBe(2);
        report.Revision!.Value.ShouldBe(3);
        Manifest head = await world.HeadAsync(shareId);
        Text(head, world, shareId, "docs/readme.md").ShouldBe("edited on the laptop");
        Text(head, world, shareId, $"{Target}/a.log").ShouldBe("a1\n");
    }

    [Fact]
    public async Task RetriesExhausted_Should_FailAfterTwentyAttempts_AndARerunReusesTheObjects()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        world.Store.BeforeReplaceManifest = world.CommitElsewhereAsync;

        (await server.PushAsync("logs")).ShouldFail().ShouldBe(SyncErrors.CommitRetriesExhausted(20));
        world.Time.Delays.Count.ShouldBe(19);
        world.Time.Delays.ShouldAllBe(delay => delay <= new MySync.Application.Sync.SyncOptions().CommitRetryMaxDelay);
        int uploads = world.Store.ObjectUploads;

        world.Store.BeforeReplaceManifest = null;
        PushReport rerun = (await server.PushAsync("logs")).ShouldSucceed();

        rerun.Uploaded.Files.ShouldBe(0);
        rerun.AlreadyStored.Files.ShouldBe(2);
        world.Store.ObjectUploads.ShouldBe(uploads); // even the record: same push, same bytes
        (await world.HeadAsync(shareId)).Entries.ContainsKey(SharePath.Create($"{Target}/a.log").Value).ShouldBeTrue();
    }

    [Fact]
    public async Task PushingTheSameFilesTwice_Should_MakeTwoFolders_AndUploadOnlyTheNewRecord()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        (await server.PushAsync("logs")).ShouldSucceed();
        Manifest afterFirst = await world.HeadAsync(shareId);
        int uploads = world.Store.ObjectUploads;
        world.Clock.Advance(TimeSpan.FromHours(1));

        PushReport second = (await server.PushAsync("logs")).ShouldSucceed();

        second.Uploaded.Files.ShouldBe(0);
        second.AlreadyStored.ShouldBe(new TransferTotals(2, 6));
        world.Store.ObjectUploads.ShouldBe(uploads + 1); // the record names its own folder
        Manifest head = await world.HeadAsync(shareId);
        foreach ((SharePath path, ManifestEntry entry) in afterFirst.Entries)
        {
            head.Entries[path].ShouldBe(entry);
        }

        head.Entries.Keys.Count(path => path.Value.StartsWith("web01/20261116-100000Z/", StringComparison.Ordinal)).ShouldBe(4);
    }

    [Fact]
    public async Task AGrowingFile_Should_BeCapturedAsItWasWhenOpened()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.BeforeOpen = (path, _) =>
        {
            if (path == "/srv/app/a.log")
            {
                server.Sources.AppendToFile(path, "more\n"); // the application keeps writing
            }
        };

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Skipped.ShouldBeEmpty();
        Manifest head = await world.HeadAsync(shareId);
        var file = (FileEntry)head.Entries[SharePath.Create($"{Target}/a.log").Value];
        file.Size.ShouldBe(8);
        file.Sha256.ShouldBe(ManifestBuilder.HashOf("a1\nmore\n"));
        Text(head, world, shareId, $"{Target}/a.log").ShouldBe("a1\nmore\n");
        server.Sources.Opens("/srv/app/a.log").ShouldBe(2); // hashed once, uploaded once
    }

    [Fact]
    public async Task AFileReplacedOnEveryRead_Should_BeRetriedThreeTimes_ThenSkippedAsUnstable()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.BeforeOpen = (path, count) =>
        {
            if (path == "/srv/app/a.log")
            {
                server.Sources.WriteFile(path, new string('x', 50 - count)); // rotated: shorter, different bytes
            }
        };

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        SkippedPath skip = report.Skipped.ShouldHaveSingleItem();
        skip.Path.ShouldBe("a.log");
        skip.Cause.ShouldBe(SkipCause.Unstable);
        skip.Detail.ShouldContain("3 times");
        server.Sources.Opens("/srv/app/a.log").ShouldBe(6);
        report.Files.ShouldBe(1);
        Manifest head = await world.HeadAsync(shareId);
        head.Entries.ContainsKey(SharePath.Create($"{Target}/a.log").Value).ShouldBeFalse();
        head.Entries.ContainsKey(SharePath.Create($"{Target}/sub/b.log").Value).ShouldBeTrue();
        server.PushRecords.Records[^1].Skipped.ShouldBe([new PushRecordSkip("a.log", "unstable", skip.Detail)]);
    }

    [Fact]
    public async Task AFileThatShrankWhileBeingHashed_Should_BeReadAgain()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        int shortReads = 0;
        server.Sources.ReadStream = (path, bytes) => path == "/srv/app/a.log" && shortReads++ == 0
            ? new ClaimsMoreThanItHas(bytes, claimed: 100)
            : new MemoryStream(bytes);

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Skipped.ShouldBeEmpty();
        server.Sources.Opens("/srv/app/a.log").ShouldBe(3); // short read, then hashed and uploaded
        Text(await world.HeadAsync(shareId), world, shareId, $"{Target}/a.log").ShouldBe("a1\n");
    }

    [Fact]
    public async Task LockedVanishedSymlinkedAndBadlyNamedFiles_Should_BeSkipped_AndTheRestPushed()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources
            .WriteFile("/srv/app/locked.log", "l")
            .WriteFile("/srv/app/gone.log", "g")
            .AddSymlink("/srv/app/link")
            .WriteFile("/srv/app/bad\\name.log", "b");
        server.Sources.Locked.Add("/srv/app/locked.log");
        server.Sources.BeforeOpen = (path, _) =>
        {
            if (path == "/srv/app/gone.log")
            {
                server.Sources.Remove(path);
            }
        };

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Skipped.Select(skip => (skip.Path, skip.Cause)).ShouldBe([
            ("bad\\name.log", SkipCause.InvalidName),
            ("gone.log", SkipCause.Unreadable),
            ("link", SkipCause.Symlink),
            ("locked.log", SkipCause.Unreadable)]);
        report.Skipped.Single(skip => skip.Path == "locked.log").Detail.ShouldContain("locked");
        report.Skipped.Single(skip => skip.Path == "gone.log").Detail.ShouldContain("removed");
        report.Files.ShouldBe(2);
        report.Committed.ShouldBeTrue();
        Paths(await world.HeadAsync(shareId)).ShouldContain($"{Target}/a.log");
    }

    [Fact]
    public async Task ASymlinkArgument_Should_BeSkipped()
    {
        (_, _, Machine server, _) = await LaptopAndServer();
        server.Sources.AddSymlink("/var/log/current");

        PushReport report = (await server.PushAsync("logs", "/srv/app/a.log", "/var/log/current")).ShouldSucceed();

        report.Skipped.ShouldHaveSingleItem().ShouldBe(new SkippedPath("current", SkipCause.Symlink, "symlinks and junctions are not pushed"));
        report.Files.ShouldBe(1);
    }

    [Fact]
    public async Task OnlyMysyncsArtifactsAndOsClutter_Should_BeLeftOut_AndALocalMysyncignoreIsAnOrdinaryFile()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources
            .WriteFile("/srv/app/.mysyncignore", "*.log\n")
            .WriteFile("/srv/app/.DS_Store", "x")
            .WriteFile("/srv/app/sub/Thumbs.db", "x")
            .WriteFile("/srv/app/sub/._b.log", "x")
            .WriteFile("/srv/app/sub/.mysynctool.log", "x")
            .WriteFile("/srv/app/nested/.mysynctool.lock", "x")
            .WriteFile("/srv/app/.env", "not a secret here, just a file");

        (await server.PushAsync("logs")).ShouldSucceed();

        Paths(await world.HeadAsync(shareId)).Where(path => path.StartsWith(Target, StringComparison.Ordinal)).ShouldBe([
            $"{Target}/.env",
            $"{Target}/.mysyncignore",
            $"{Target}/_mysync-push.json",
            $"{Target}/a.log",
            $"{Target}/empty",
            $"{Target}/nested", // only an artifact inside: empty
            $"{Target}/sub/b.log"]);
        server.IgnoreRules.Compiled.ShouldBeEmpty(); // the pushed .mysyncignore was never applied
    }

    [Fact]
    public async Task TheSharesIgnoreRules_Should_StillPush_ButWarn_AndAClonesSyncSkipsThem()
    {
        (SyncWorld world, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        laptop.Folder.WriteFile(".mysyncignore", "*.log\n");
        (await laptop.SyncAsync()).ShouldSucceed();
        server.Sources.WriteFile("/srv/app/notes.txt", "n");

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.IgnoredByShare.ShouldBe(2);
        report.Warnings.ShouldHaveSingleItem().ShouldBe(
            $"2 pushed files match the share's .mysyncignore, so machines that sync this share won't download them: {Target}/a.log, {Target}/sub/b.log");
        server.IgnoreRules.Compiled.ShouldBe(["*.log\n"]);
        Paths(await world.HeadAsync(shareId)).ShouldContain($"{Target}/a.log");

        Machine clone = world.Machine("ali@SERVER01");
        clone.IgnoreRules.Matcher = new SimplePatternIgnoreMatcher("*.log\n");
        (await clone.CloneAsync("logs")).ShouldSucceed();
        clone.Folder.Exists($"{Target}/notes.txt").ShouldBeTrue();
        clone.Folder.Exists($"{Target}/a.log").ShouldBeFalse();
    }

    [Fact]
    public async Task TheSharesGitignore_Should_CountLikeItsMysyncignore()
    {
        (_, Machine laptop, Machine server, _) = await LaptopAndServer();
        laptop.Folder.WriteFile(".gitignore", "sub/\n");
        (await laptop.SyncAsync()).ShouldSucceed();

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.IgnoredByShare.ShouldBe(1);
        report.Warnings.ShouldHaveSingleItem().ShouldBe(
            $"1 pushed files match the share's .gitignore, so machines that sync this share won't download them: {Target}/sub/b.log");
        server.IgnoreRules.Compiled.ShouldBe(["sub/\n"]);
    }

    [Fact]
    public async Task BothOfTheSharesIgnoreFiles_Should_BeJoinedAsAUnion()
    {
        (_, Machine laptop, Machine server, _) = await LaptopAndServer();
        laptop.Folder.WriteFile(".mysyncignore", "a.log\n").WriteFile(".gitignore", "sub/\n");
        (await laptop.SyncAsync()).ShouldSucceed();
        server.Sources.WriteFile("/srv/app/notes.txt", "n");

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.IgnoredByShare.ShouldBe(2);
        report.Warnings.ShouldHaveSingleItem().ShouldBe(
            $"2 pushed files match the share's .mysyncignore and .gitignore, so machines that sync this share won't download them: {Target}/a.log, {Target}/sub/b.log");
        server.IgnoreRules.Compiled.ShouldBe(["a.log\n", "sub/\n"]);
    }

    [Fact]
    public async Task AShareWithoutIgnoreRules_Should_GiveNoIgnoreWarning()
    {
        (_, _, Machine server, _) = await LaptopAndServer();

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.IgnoredByShare.ShouldBe(0);
        report.Warnings.ShouldBeEmpty();
        server.IgnoreRules.Compiled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AClone_Should_DownloadThePushedFolder_AndSyncEditsAndDeletesOfItNormally()
    {
        (SyncWorld world, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        (await server.PushAsync("logs")).ShouldSucceed();

        SyncReport pulled = (await laptop.SyncAsync()).ShouldSucceed();

        pulled.Conflicts.ShouldBeEmpty();
        laptop.Folder.ReadText($"{Target}/a.log").ShouldBe("a1\n");
        laptop.Folder.ReadText($"{Target}/sub/b.log").ShouldBe("b1\n");
        laptop.Folder.IsDirectory($"{Target}/empty").ShouldBeTrue();

        laptop.Folder.Remove($"{Target}/sub/b.log");
        laptop.Folder.WriteFile($"{Target}/a.log", "annotated\n");
        SyncReport pushedBack = (await laptop.SyncAsync()).ShouldSucceed();

        pushedBack.DeletedRemotely.ShouldBe(1);
        Manifest head = await world.HeadAsync(shareId);
        head.Entries.ContainsKey(SharePath.Create($"{Target}/sub/b.log").Value).ShouldBeFalse();
        Text(head, world, shareId, $"{Target}/a.log").ShouldBe("annotated\n");
    }

    [Fact]
    public async Task Create_Should_ClaimTheNameAndPushIntoTheNewShare()
    {
        SyncWorld world = new();
        Machine server = Server(world, "svc@web01");

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true))).ShouldSucceed();

        report.Created.ShouldBeTrue();
        report.Committed.ShouldBeTrue();
        report.Revision!.Value.ShouldBe(2); // an empty revision 1 at once, then the push
        Abstractions.Storage.AliasSnapshot alias = (await world.Aliases.GetAsync(ShareName.Create("incident-logs").Value, SyncWorld.Ct)).Value!;
        alias.Alias.ShareId.ShouldBe(report.ShareId!);
        alias.Alias.CreatedBy.ShouldBe("svc@web01");
        Manifest head = await world.HeadAsync(report.ShareId!);
        Paths(head).ShouldContain($"{Target}/a.log");
        world.Store.BlobNames.ShouldContain($"shares/{report.ShareId}/manifests/0000000001.json");
    }

    [Fact]
    public async Task CreateLosingTheRace_Should_PushIntoTheShareTheOtherMachineMade()
    {
        SyncWorld world = new();
        Machine server = Server(world, "svc@web01");
        Machine other = Server(world, "svc@web02");
        world.Aliases.BeforeCreate = async name =>
        {
            world.Aliases.BeforeCreate = null;
            (await other.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true))).ShouldSucceed();
        };

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true))).ShouldSucceed();

        report.Created.ShouldBeFalse();
        Manifest head = await world.HeadAsync(report.ShareId!);
        head.Revision.Value.ShouldBe(3);
        Paths(head).ShouldContain($"web01/{Stamp}/a.log");
        Paths(head).ShouldContain($"web02/{Stamp}/a.log");
    }

    [Fact]
    public async Task Create_Should_NeedAName_AndAMissingShareWithoutItIsNotFound()
    {
        SyncWorld world = new();
        Machine server = Server(world, "svc@web01");

        (await server.PushAsync(new PushCommand(ShareReference.For(ShareId.New()), [], Create: true))).ShouldFail().Code.ShouldBe("Push.CreateNeedsName");
        (await server.PushAsync("incident-logs")).ShouldFail().Code.ShouldBe("Shares.NameNotFound");
        (await server.PushAsync(ShareId.New().ToString())).ShouldFail().Code.ShouldBe("Shares.NotFound");
        world.Store.ObjectUploads.ShouldBe(0);
        server.Sources.TotalOpens.ShouldBe(0);
    }

    [Fact]
    public async Task AnUnfinishedShare_Should_BeIncomplete()
    {
        SyncWorld world = new();
        Machine server = Server(world, "svc@web01");
        (await world.Aliases.CreateAsync(new AliasRecord(ShareName.Create("half").Value, ShareId.New(), DateTimeOffset.UnixEpoch, "x@y"), SyncWorld.Ct)).IsSuccess.ShouldBeTrue();

        (await server.PushAsync("half")).ShouldFail().Code.ShouldBe("Shares.Incomplete");
    }

    [Fact]
    public async Task ADeletedShare_Should_BeRefusedBeforeAnythingIsRead()
    {
        (_, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        (await laptop.DeleteAsync()).ShouldSucceed();

        (await server.PushAsync(shareId.ToString())).ShouldFail().Code.ShouldBe("Shares.Deleted");
        server.Sources.TotalOpens.ShouldBe(0);
    }

    [Fact]
    public async Task AShareDeletedWhilePushing_Should_FailAndRemoveWhatThisPushUploaded()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        world.Store.BeforeReplaceManifest = async id =>
        {
            world.Store.BeforeReplaceManifest = null;
            Abstractions.Storage.ManifestSnapshot head = (await world.Store.GetManifestAsync(id, SyncWorld.Ct)).Value!;
            Manifest marker = head.Manifest.CreateDeletedRevision(DateTimeOffset.UnixEpoch, "x@y").Value;
            (await world.Store.ReplaceManifestAsync(id, marker, head.ETag, SyncWorld.Ct)).IsSuccess.ShouldBeTrue();
        };

        (await server.PushAsync("logs")).ShouldFail().Code.ShouldBe("Shares.Deleted");

        foreach (string content in new[] { "a1\n", "b1\n" })
        {
            (await world.Store.ObjectExistsAsync(shareId, ManifestBuilder.HashOf(content), SyncWorld.Ct)).Value.ShouldBeFalse();
        }

        (await world.Store.ObjectExistsAsync(shareId, ManifestBuilder.HashOf("hello"), SyncWorld.Ct)).Value.ShouldBeTrue(); // not this push's
    }

    [Fact]
    public async Task ADryRun_Should_ReadEverything_AndWriteNothingAnywhere()
    {
        (SyncWorld world, _, Machine server, _) = await LaptopAndServer();
        server.Sources.WriteFile("/srv/app/hello.txt", "hello"); // already stored by the laptop
        IReadOnlyList<string> blobs = world.Store.BlobNames;

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], DryRun: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        report.Target.ShouldBe(Target);
        report.Revision!.Value.ShouldBe(1);
        report.Files.ShouldBe(3);
        report.Uploaded.ShouldBe(new TransferTotals(2, 6));
        report.AlreadyStored.ShouldBe(new TransferTotals(1, 5));
        world.Store.BlobNames.ShouldBe(blobs);
        server.PushRecords.Records.ShouldBeEmpty();
        server.Sources.Opens("/srv/app/a.log").ShouldBe(1); // read and hashed, not uploaded
    }

    [Fact]
    public async Task ADryRunOfCreate_Should_CreateNothing()
    {
        SyncWorld world = new();
        Machine server = Server(world, "svc@web01");

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true, DryRun: true))).ShouldSucceed();

        report.Created.ShouldBeTrue();
        report.ShareId.ShouldBeNull();
        report.Files.ShouldBe(2);
        world.Aliases.BlobNames.ShouldBeEmpty();
        world.Store.BlobNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADryRun_Should_StillFailWhereAPushWould()
    {
        (_, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        laptop.Folder.WriteFile("web01", "a file where the host folder would go");
        (await laptop.SyncAsync()).ShouldSucceed();

        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], DryRun: true))).ShouldFail().Code.ShouldBe("Push.TargetIsAFile");
        server.Sources.SyncedWith("/srv/app", shareId);
        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Host: "other", DryRun: true))).ShouldFail().Code.ShouldBe("Push.SourceIsTheShare");
    }

    [Fact]
    public async Task ThePushRecord_Should_SayWhereTheDataCameFrom()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.Locked.Add("/srv/app/sub/b.log");

        (await server.PushAsync("logs", "/srv/app/sub", "a.log")).ShouldSucceed();

        PushRecord record = server.PushRecords.Records.ShouldHaveSingleItem();
        record.Host.ShouldBe("web01");
        record.User.ShouldBe("svc");
        record.OperatingSystem.ShouldBe("TestOS 1.0 (test-x64)");
        record.MysyncVersion.ShouldBe("0.0.0-test");
        record.StartedUtc.ShouldBe(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        record.Sources.ShouldBe(["/srv/app/sub", "/srv/app/a.log"]);
        record.Target.ShouldBe(Target);
        record.Files.ShouldBe(1);
        record.Bytes.ShouldBe(3);
        record.Skipped.ShouldBe([new PushRecordSkip("sub/b.log", "unreadable", record.Skipped[0].Detail)]);
        Text(await world.HeadAsync(shareId), world, shareId, $"{Target}/_mysync-push.json").ShouldContain($"target={Target}");
    }

    [Fact]
    public async Task APushedFileNamedLikeTheRecord_Should_WinOverTheRecord()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.WriteFile("/srv/app/_mysync-push.json", "mine");

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Warnings.ShouldHaveSingleItem().ShouldContain("push record was left out");
        Text(await world.HeadAsync(shareId), world, shareId, $"{Target}/_mysync-push.json").ShouldBe("mine");
        server.PushRecords.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoInfo_Should_LeaveTheRecordOut()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();

        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], WriteInfo: false))).ShouldSucceed();

        Paths(await world.HeadAsync(shareId)).ShouldNotContain($"{Target}/_mysync-push.json");
        server.PushRecords.Records.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/srv/app")] // the current folder itself
    [InlineData("/srv/app/sub")] // a folder inside it
    [InlineData("/srv/app/sub/b.log")] // a file inside it
    public async Task ASourceThatSyncsWithTheTargetShare_Should_BeRefusedBeforeAnyRead_EvenWithForce(string source)
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.SyncedWith("/srv/app", shareId);
        int uploads = world.Store.ObjectUploads;
        string[] paths = source == "/srv/app" ? [] : [source];

        SharedKernel.Error error = (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, paths, Force: true))).ShouldFail();

        error.Code.ShouldBe("Push.SourceIsTheShare");
        error.Description.ShouldContain("/srv/app");
        error.Description.ShouldContain("logs");
        server.Sources.TotalOpens.ShouldBe(0);
        world.Store.ObjectUploads.ShouldBe(uploads);
    }

    [Fact]
    public async Task AFolderThatSyncsWithAnotherShare_Should_BePushed_ItsStateUntouchedAndNotUploaded()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.SyncedWith("/srv/app", ShareId.New());
        byte[] state = server.Sources.ReadBytes("/srv/app/.mysynctool");

        (await server.PushAsync("logs")).ShouldSucceed();

        server.Sources.ReadBytes("/srv/app/.mysynctool").ShouldBe(state);
        Paths(await world.HeadAsync(shareId)).ShouldNotContain(path => path.Contains(".mysynctool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADamagedStateFileAboveASource_Should_StopThePush()
    {
        (_, _, Machine server, _) = await LaptopAndServer();
        server.Sources.SyncedWith("/srv", shareId: null);

        (await server.PushAsync("logs")).ShouldFail().Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public async Task APush_Should_NeverTouchSyncStateLockLogOrStaging()
    {
        (_, _, Machine server, _) = await LaptopAndServer();
        server.Sources.WriteFile("/srv/app/x.log", "x");

        (await server.PushAsync("logs")).ShouldSucceed();
        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], DryRun: true))).ShouldSucceed();

        server.State.Saves.ShouldBe(0);
        server.State.State.ShouldBeNull();
        server.Lock.Acquisitions.ShouldBe(0);
        server.ConflictLog.Lines.ShouldBeEmpty();
        server.ConflictLog.Deleted.ShouldBeFalse();
        server.Folder.StagingCleans.ShouldBe(0);
        server.Folder.AllPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingAndClashingSources_Should_FailBeforeAnythingIsRead()
    {
        (_, _, Machine server, _) = await LaptopAndServer();
        server.Sources.WriteFile("/a/app/x", "1").WriteFile("/b/app/y", "2").WriteFile("/c/app", "3");

        (await server.PushAsync("logs", "missing.log")).ShouldFail().ShouldBe(PushErrors.SourceNotFound("missing.log"));
        (await server.PushAsync("logs", "/a/app", "/b/app")).ShouldFail().Code.ShouldBe("Push.DuplicateSource");
        (await server.PushAsync("logs", "/a/app", "/c/app")).ShouldFail().Code.ShouldBe("Push.DuplicateSource");
        (await server.PushAsync("logs", "a.log", "./a.log")).ShouldFail().Code.ShouldBe("Push.DuplicateSource");
        server.Sources.TotalOpens.ShouldBe(0);
    }

    [Fact]
    public async Task AFileNamedLikeTheHost_Should_BeTargetIsAFile()
    {
        (_, Machine laptop, Machine server, _) = await LaptopAndServer();
        laptop.Folder.WriteFile("web01", "x");
        (await laptop.SyncAsync()).ShouldSucceed();

        (await server.PushAsync("logs")).ShouldFail().Code.ShouldBe("Push.TargetIsAFile");
        server.Sources.TotalOpens.ShouldBe(0);
        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Host: "web01b"))).ShouldSucceed();
    }

    [Fact]
    public async Task AnUnsafeRoot_Should_NeedForce()
    {
        (_, _, Machine server, _) = await LaptopAndServer();
        server.Sources.CurrentDirectory = "/";

        (await server.PushAsync("logs")).ShouldFail().Code.ShouldBe("Shares.UnsafeRoot");
        server.Sources.UnsafeRoots.Add("/home/svc");
        server.Sources.AddFolder("/home/svc");
        (await server.PushAsync("logs", "/srv/app/a.log", "/home/svc")).ShouldFail().Code.ShouldBe("Shares.UnsafeRoot");
        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Force: true))).ShouldSucceed().Files.ShouldBe(2);
    }

    [Fact]
    public async Task NothingToPush_Should_CommitNothing()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.AddFolder("/srv/empty");
        server.Sources.CurrentDirectory = "/srv/empty";

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.NothingToPush.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        report.Revision!.Value.ShouldBe(1);
        (await world.HeadAsync(shareId)).Revision.Value.ShouldBe(1);
    }

    [Fact]
    public async Task CreateWithNothingToPush_Should_CreateNothing()
    {
        SyncWorld world = new();
        Machine server = world.Machine("svc@web01");
        server.Sources.AddFolder("/srv/app");

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true))).ShouldSucceed();

        report.NothingToPush.ShouldBeTrue();
        report.Created.ShouldBeFalse();
        world.Aliases.BlobNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task NamesOtherMachinesCantHold_Should_BePushedWithWarnings()
    {
        (SyncWorld world, _, Machine server, ShareId shareId) = await LaptopAndServer();
        server.Sources.WriteFile("/srv/app/what?.log", "w").WriteFile("/srv/app/Readme.txt", "1").WriteFile("/srv/app/README.txt", "2");

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Warnings.Length.ShouldBe(2);
        report.Warnings.ShouldContain(warning => warning.Contains("what?.log", StringComparison.Ordinal) && warning.Contains("Windows", StringComparison.Ordinal));
        report.Warnings.ShouldContain(warning => warning.Contains("README.txt", StringComparison.Ordinal) && warning.Contains("case", StringComparison.Ordinal));
        Paths(await world.HeadAsync(shareId)).ShouldContain($"{Target}/what?.log");
    }

    [Fact]
    public async Task ACompressedShare_Should_GetCompressedPushes()
    {
        (SyncWorld world, Machine laptop, Machine server, ShareId shareId) = await LaptopAndServer();
        (await laptop.SetCompressionAsync(on: true)).ShouldSucceed();
        server.Sources.WriteFile("/srv/app/big.log", string.Concat(Enumerable.Repeat("the same line again\n", 500)));

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Uploaded.Compressed.ShouldBeTrue();
        world.Store.ContentEncodingOf(shareId, ManifestBuilder.HashOf(string.Concat(Enumerable.Repeat("the same line again\n", 500)))).ShouldBe("gzip");
        Text(await world.HeadAsync(shareId), world, shareId, $"{Target}/big.log").Length.ShouldBe(10_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyHost_Should_BeAValidationError(string host)
    {
        (_, _, Machine server, _) = await LaptopAndServer();

        SharedKernel.Error error = (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Host: host))).ShouldFail();

        error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
    }

    [Fact]
    public async Task ParallelismOutOfRange_Should_BeAValidationError()
    {
        (_, _, Machine server, _) = await LaptopAndServer();

        (await server.PushAsync(new PushCommand(ShareReference.Parse("logs").Value, [], Parallelism: 0))).ShouldFail().Type.ShouldBe(SharedKernel.ErrorType.Validation);
    }

    /// <summary>Says it is longer than it is: a file truncated between the open and the read.</summary>
    private sealed class ClaimsMoreThanItHas(byte[] bytes, long claimed) : MemoryStream(bytes)
    {
        public override long Length => claimed;
    }
}
