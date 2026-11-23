using System.Text;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Push;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync.Push;
using MySync.Testing;
using Shouldly;
using SharedKernel;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// <c>mysync push</c>'s filters (CLAUDE.md §10b): <c>--updated-since</c>, <c>--include</c>,
/// <c>--exclude</c> and <c>--max-size</c>, applied to the listing before anything is opened.
/// </summary>
public class PushFilterTests
{
    // SyncWorld's clock: 2026-11-16 09:00:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private const string Target = "web01/20261116-090000Z";

    private static ShareReference Logs => ShareReference.Parse("logs").Value;

    /// <summary>
    /// A share <c>logs</c> and a server whose <c>/srv/app</c> holds files of known ages and sizes:
    /// <c>new.log</c> (30 min, 3 B), <c>old.log</c> (3 h, 3 B), <c>big.log</c> (30 min, 2000 B),
    /// <c>notes.txt</c> (30 min, 5 B), <c>archive/a.log.gz</c> (30 min, 2 B), and an empty folder.
    /// </summary>
    private static async Task<(SyncWorld World, Machine Server, ShareId ShareId)> Setup(string serverName = "svc@web01")
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder.WriteFile("docs/readme.md", "hello");
        CreateShareResponse created = (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("logs").Value))).ShouldSucceed();

        Machine server = world.Machine(serverName);
        WriteFiles(server.Sources);
        return (world, server, created.ShareId);
    }

    private static void WriteFiles(InMemoryLocalSources sources) => sources
        .WriteFile("/srv/app/new.log", "new", mtimeUtc: Now.AddMinutes(-30))
        .WriteFile("/srv/app/old.log", "old", mtimeUtc: Now.AddHours(-3))
        .WriteFile("/srv/app/big.log", new string('x', 2000), mtimeUtc: Now.AddMinutes(-30))
        .WriteFile("/srv/app/notes.txt", "notes", mtimeUtc: Now.AddMinutes(-30))
        .WriteFile("/srv/app/archive/a.log.gz", "gz", mtimeUtc: Now.AddMinutes(-30))
        .AddFolder("/srv/app/empty");

    private static IEnumerable<string> Pushed(Manifest head, string folder = Target) =>
        head.Entries.Keys.Select(path => path.Value)
            .Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal))
            .Select(path => path[(folder.Length + 1)..])
            .Where(path => path != PushRecord.FileName);

    private static string Text(Manifest manifest, SyncWorld world, ShareId shareId, string path)
    {
        var file = (FileEntry)manifest.Entries[SharePath.Create(path).Value];
        return Encoding.UTF8.GetString(world.Store.ReadObject(shareId, file.Sha256));
    }

    [Fact]
    public async Task UpdatedSince_ADuration_Should_PushOnlyFilesModifiedSinceThen()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "1h"))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["archive/a.log.gz", "big.log", "new.log", "notes.txt"]);
        report.Files.ShouldBe(4);
        report.Filters.ShouldNotBeNull();
        report.Filters.UpdatedSinceUtc.ShouldBe(Now.AddHours(-1));
        report.Filters.UpdatedSinceSpec.ShouldBe("1h");
        report.FilteredOut.ShouldBe(new PushFilteredOut(NotChanged: 1, NotIncluded: 0, Excluded: 0, TooLarge: 0));
        report.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdatedSince_ATime_Should_UseThatInstant()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "2026-11-16T05:00:00Z"))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldContain("old.log");
        report.Filters!.UpdatedSinceUtc.ShouldBe(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero));
        report.FilteredOut.Total.ShouldBe(0);
    }

    [Fact]
    public async Task UpdatedSince_ATimeInTheFuture_Should_BeRefused_BeforeAnythingIsRead()
    {
        (SyncWorld world, Machine server, _) = await Setup();
        int uploads = world.Store.ObjectUploads;

        Error error = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "2026-11-16T09:00:01Z"))).ShouldFail();

        error.Code.ShouldBe("Push.InvalidSince");
        error.Description.ShouldContain("in the future");
        server.Sources.TotalOpens.ShouldBe(0);
        world.Store.ObjectUploads.ShouldBe(uploads);
    }

    [Fact]
    public async Task Include_Should_PushOnlyMatchingFiles()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Include: ["*.log"]))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["big.log", "new.log", "old.log"]);
        report.FilteredOut.ShouldBe(new PushFilteredOut(0, NotIncluded: 2, 0, 0));
        report.Filters!.Include.ShouldBe(["*.log"]);
        server.IgnoreRules.Compiled.ShouldBe(["*.log"]);
    }

    [Fact]
    public async Task Exclude_Should_LeaveOutMatchingFiles_AndWholeFolders()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Exclude: ["*.txt", "archive/"]))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["big.log", "new.log", "old.log"]);
        report.FilteredOut.ShouldBe(new PushFilteredOut(0, 0, Excluded: 2, 0));
        server.IgnoreRules.Compiled.ShouldBe(["*.txt\narchive/"]);
    }

    [Fact]
    public async Task Exclude_Should_WinOverInclude()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Include: ["*.log"], Exclude: ["big.log"]))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["new.log", "old.log"]);
        report.FilteredOut.ShouldBe(new PushFilteredOut(0, NotIncluded: 2, Excluded: 1, 0));
    }

    [Fact]
    public async Task Patterns_Should_MatchThePathUnderTheTargetFolder()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        server.Sources.WriteFile("/var/log/app/keep.log", "k").WriteFile("/var/log/app/other.log", "o").WriteFile("/var/log/app/sub/keep.log", "s");

        (await server.PushAsync(new PushCommand(Logs, ["/var/log/app"], Include: ["app/keep.log"]))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["app/keep.log"]);
    }

    [Fact]
    public async Task MaxSize_Should_LeaveOutLargerFiles()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], MaxSize: "1K"))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldNotContain("big.log");
        report.Files.ShouldBe(4);
        report.FilteredOut.ShouldBe(new PushFilteredOut(0, 0, 0, TooLarge: 1));
        report.Filters!.MaxSizeBytes.ShouldBe(1024);
    }

    [Fact]
    public async Task AllFourTogether_Should_PushExactlyWhatPassesThemAll_CountingEachFileOnce()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "1h", Include: ["*.log", "*.gz"], Exclude: ["archive/"], MaxSize: "1KB"))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBe(["new.log"]);
        report.FilteredOut.ShouldBe(new PushFilteredOut(NotChanged: 1, NotIncluded: 1, Excluded: 1, TooLarge: 1));
        report.FilteredOut.Total.ShouldBe(4);

        PushRecord record = server.PushRecords.Records.ShouldHaveSingleItem();
        record.Files.ShouldBe(1);
        record.Filters.ShouldBe(report.Filters);
        record.FilteredOut.ShouldBe(report.FilteredOut);
        record.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task FilteredOutFiles_Should_NeverBeOpened_NorCountAsSkipped()
    {
        (_, Machine server, _) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Exclude: ["*.log"]))).ShouldSucceed();

        foreach (string left in new[] { "new.log", "old.log", "big.log" })
        {
            server.Sources.Opens($"/srv/app/{left}").ShouldBe(0);
        }

        server.Sources.Opens("/srv/app/notes.txt").ShouldBeGreaterThan(0); // hashed, then uploaded
        report.Skipped.ShouldBeEmpty();
        report.FilteredOut.Excluded.ShouldBe(3);
    }

    [Fact]
    public async Task ANamedFileAFilterLeavesOut_Should_GetAWarningNamingItAndTheFilter()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport report = (await server.PushAsync(new PushCommand(Logs, ["old.log", "notes.txt", "archive"], UpdatedSince: "1h", Exclude: ["*.txt", "*.gz"]))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId)).ShouldBeEmpty();
        report.NothingToPush.ShouldBeTrue();
        report.Warnings.ShouldBe([
            "'notes.txt' was named, but --exclude '*.txt' left it out, so it wasn't pushed.",
            "'old.log' was named, but --updated-since 1h left it out (modified 2026-11-16 06:00:00 UTC, before 2026-11-16 08:00:00 UTC), so it wasn't pushed."]);
    }

    [Fact]
    public async Task ANamedFileLeftOutByIncludeOrSize_Should_SaySo()
    {
        (_, Machine server, _) = await Setup();

        PushReport include = (await server.PushAsync(new PushCommand(Logs, ["notes.txt", "new.log"], Include: ["*.log"]))).ShouldSucceed();
        PushReport size = (await server.PushAsync(new PushCommand(Logs, ["big.log"], MaxSize: "1K"))).ShouldSucceed();

        include.Warnings.ShouldBe(["'notes.txt' was named, but it matches no --include pattern ('*.log'), so it wasn't pushed."]);
        size.Warnings.ShouldBe(["'big.log' was named, but --max-size 1K left it out (it is 2000 bytes), so it wasn't pushed."]);
    }

    [Fact]
    public async Task EverythingFilteredOut_Should_CommitNothing_AndSaySo()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        Manifest before = await world.HeadAsync(shareId);
        int uploads = world.Store.ObjectUploads;

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Include: ["*.csv"]))).ShouldSucceed();

        report.NothingToPush.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        report.FilteredOut.ShouldBe(new PushFilteredOut(0, NotIncluded: 5, 0, 0));
        report.Skipped.ShouldBeEmpty();
        (await world.HeadAsync(shareId)).Revision.ShouldBe(before.Revision);
        world.Store.ObjectUploads.ShouldBe(uploads);
        server.Sources.TotalOpens.ShouldBe(0);
        server.PushRecords.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyFolders_Should_BeLeftOutWithAFilter_AndKeptWithout()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        (await server.PushAsync(new PushCommand(Logs, [], MaxSize: "1GB"))).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        PushReport plain = (await server.PushAsync("logs")).ShouldSucceed();

        Manifest head = await world.HeadAsync(shareId);
        Pushed(head).ShouldNotContain("empty");
        Pushed(head, plain.Target).ShouldContain("empty");
    }

    [Fact]
    public async Task AnUnfilteredPush_Should_ReportAndRecordExactlyAsBefore()
    {
        (_, Machine server, _) = await Setup();

        PushReport report = (await server.PushAsync("logs")).ShouldSucceed();

        report.Filters.ShouldBeNull();
        report.FilteredOut.ShouldBe(PushFilteredOut.None);
        report.Planned.ShouldBeEmpty();
        report.Files.ShouldBe(5);
        PushRecord record = server.PushRecords.Records.ShouldHaveSingleItem();
        record.Filters.ShouldBeNull();
        record.FilteredOut.ShouldBeNull();
        (record.Host, record.Target, record.Files, record.Bytes).ShouldBe(("web01", Target, 5, 2013L));
        record.Sources.ShouldBe(["/srv/app"]);
        record.Skipped.ShouldBeEmpty();
        server.IgnoreRules.Compiled.ShouldBeEmpty();
    }

    [Fact]
    public async Task DryRun_WithFilters_Should_ShowWhatWouldGo_AndWriteNothing()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        Manifest before = await world.HeadAsync(shareId);
        int uploads = world.Store.ObjectUploads;

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], DryRun: true, UpdatedSince: "1h", Exclude: ["*.gz"]))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Committed.ShouldBeFalse();
        report.Planned.ShouldBe(["big.log", "new.log", "notes.txt"]);
        report.FilteredOut.ShouldBe(new PushFilteredOut(NotChanged: 1, 0, Excluded: 1, 0));
        report.Filters!.UpdatedSinceUtc.ShouldBe(Now.AddHours(-1));
        (await world.HeadAsync(shareId)).Revision.ShouldBe(before.Revision);
        world.Store.ObjectUploads.ShouldBe(uploads);
        server.PushRecords.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Last_Should_PushEverythingTheFirstTime_AndThenOnlyWhatChanged()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();

        PushReport first = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "last"))).ShouldSucceed();
        first.Files.ShouldBe(5);
        first.Filters!.UpdatedSinceUtc.ShouldBeNull();
        first.Filters.UpdatedSinceSpec.ShouldBe("last");

        world.Clock.Advance(TimeSpan.FromHours(1));
        server.Sources.WriteFile("/srv/app/new.log", "new, longer", mtimeUtc: Now.AddMinutes(20)).WriteFile("/srv/app/fresh.log", "f", mtimeUtc: Now.AddMinutes(50));

        PushReport second = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "last"))).ShouldSucceed();

        second.Target.ShouldBe("web01/20261116-100000Z");
        second.Filters!.UpdatedSinceUtc.ShouldBe(Now);
        second.FilteredOut.NotChanged.ShouldBe(4);
        Manifest head = await world.HeadAsync(shareId);
        Pushed(head, second.Target).ShouldBe(["fresh.log", "new.log"]);
        Text(head, world, shareId, $"{second.Target}/new.log").ShouldBe("new, longer");
        Pushed(head, first.Target).Count().ShouldBe(5); // the first folder is untouched
    }

    [Fact]
    public async Task Last_Should_IncludeAFileModifiedAtTheStartOfThePreviousPush()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        (await server.PushAsync("logs")).ShouldSucceed();
        server.Sources.WriteFile("/srv/app/during.log", "d", mtimeUtc: Now);
        world.Clock.Advance(TimeSpan.FromMinutes(10));

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "last"))).ShouldSucceed();

        Pushed(await world.HeadAsync(shareId), report.Target).ShouldBe(["during.log"]);
    }

    [Fact]
    public async Task Last_Should_OnlyFollowThisHostsOwnPushes()
    {
        (SyncWorld world, Machine web01, ShareId shareId) = await Setup();
        Machine web02 = world.Machine("svc@web02");
        WriteFiles(web02.Sources);

        (await web01.PushAsync("logs")).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromHours(1));
        PushReport other = (await web02.PushAsync(new PushCommand(Logs, [], UpdatedSince: "last"))).ShouldSucceed();

        other.Filters!.UpdatedSinceUtc.ShouldBeNull();
        other.Files.ShouldBe(5);
        Pushed(await world.HeadAsync(shareId), other.Target).Count().ShouldBe(5);
    }

    [Fact]
    public async Task Last_WithHost_Should_UseThatHostFoldersHistory()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        (await server.PushAsync(new PushCommand(Logs, [], Host: "collector"))).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromHours(1));
        server.Sources.WriteFile("/srv/app/fresh.log", "f", mtimeUtc: Now.AddMinutes(30));

        PushReport asCollector = (await server.PushAsync(new PushCommand(Logs, [], Host: "collector", UpdatedSince: "last"))).ShouldSucceed();
        PushReport asItself = (await server.PushAsync(new PushCommand(Logs, [], UpdatedSince: "last"))).ShouldSucceed();

        asCollector.Filters!.UpdatedSinceUtc.ShouldBe(Now);
        Pushed(await world.HeadAsync(shareId), asCollector.Target).ShouldBe(["fresh.log"]);
        asItself.Filters!.UpdatedSinceUtc.ShouldBeNull();
        asItself.Files.ShouldBe(6);
    }

    [Fact]
    public async Task Last_WithCreate_Should_HaveNoCutoff()
    {
        SyncWorld world = new();
        Machine server = world.Machine("svc@web01");
        WriteFiles(server.Sources);

        PushReport report = (await server.PushAsync(new PushCommand(ShareReference.Parse("incident-logs").Value, [], Create: true, UpdatedSince: "last"))).ShouldSucceed();

        report.Created.ShouldBeTrue();
        report.Files.ShouldBe(5);
        report.Filters!.UpdatedSinceUtc.ShouldBeNull();
        Pushed(await world.HeadAsync(report.ShareId!)).ShouldNotContain("empty"); // a filter was given
    }

    [Fact]
    public async Task TheSharesIgnoreWarning_Should_CountOnlyFilesActuallyPushed()
    {
        (SyncWorld world, Machine server, ShareId shareId) = await Setup();
        Machine laptop = world.Machine("usman@DEV-LAPTOP2");
        (await laptop.CloneAsync("logs")).ShouldSucceed();
        laptop.Folder.WriteFile(".mysyncignore", "*.gz\n");
        (await laptop.SyncAsync()).ShouldSucceed();

        PushReport filtered = (await server.PushAsync(new PushCommand(Logs, [], Exclude: ["*.gz"]))).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        PushReport plain = (await server.PushAsync("logs")).ShouldSucceed();

        filtered.IgnoredByShare.ShouldBe(0);
        filtered.Warnings.ShouldBeEmpty();
        plain.IgnoredByShare.ShouldBe(1);
        (await world.HeadAsync(shareId)).Revision.Value.ShouldBe(4);
    }

    [Theory]
    [InlineData("2x", null, null, null, "Push.InvalidSince")]
    [InlineData("-1h", null, null, null, "Push.InvalidSince")]
    [InlineData(null, "!*.log", null, null, "Push.InvalidPattern")]
    [InlineData(null, "", null, null, "Push.InvalidPattern")]
    [InlineData(null, null, "#x", null, "Push.InvalidPattern")]
    [InlineData(null, null, null, "0", "Push.InvalidSize")]
    [InlineData(null, null, null, "1XB", "Push.InvalidSize")]
    public async Task InvalidValues_Should_FailInTheValidator_BeforeAnythingIsTouched(string? since, string? include, string? exclude, string? maxSize, string code)
    {
        (SyncWorld world, Machine server, _) = await Setup();
        int uploads = world.Store.ObjectUploads;

        Error error = (await server.PushAsync(new PushCommand(
            Logs, [], UpdatedSince: since, Include: include is null ? null : [include], Exclude: exclude is null ? null : [exclude], MaxSize: maxSize))).ShouldFail();

        ValidationError validation = error.ShouldBeOfType<ValidationError>();
        validation.Type.ShouldBe(ErrorType.Validation);
        validation.Errors.ShouldHaveSingleItem().Code.ShouldBe(code);
        server.Sources.TotalOpens.ShouldBe(0);
        world.Store.ObjectUploads.ShouldBe(uploads);
    }
}
