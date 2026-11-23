using MySync.Application.Abstractions.Progress;
using MySync.Application.Session;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Session;

/// <summary>
/// The folder session behind <c>mysync tui</c> (CLAUDE.md §10d): one job at a time through the
/// existing commands, events in order, progress coalesced, nothing written by a rescan.
/// </summary>
public class FolderSessionTests
{
    /// <summary>A share <c>team</c> made by a laptop, cloned by B (the machine whose session is tested).</summary>
    private static async Task<(SyncWorld World, Machine Laptop, Machine B, ShareId ShareId)> Setup()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder.WriteFile("docs/readme.md", "# readme").WriteFile("app.yml", "port: 80");
        ShareId shareId = (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("team").Value))).ShouldSucceed().ShareId;
        Machine b = world.Machine("ali@SERVER01");
        (await b.CloneAsync("team")).ShouldSucceed();
        return (world, laptop, b, shareId);
    }

    private static async Task<List<SessionEvent>> RunAsync(IFolderSession session, Func<IFolderSession, SharedKernel.Result> start)
    {
        start(session).IsSuccess.ShouldBeTrue();
        await session.WhenIdleAsync();
        return Drain(session);
    }

    private static List<SessionEvent> Drain(IFolderSession session)
    {
        List<SessionEvent> events = [];
        while (session.TryReadEvent(out SessionEvent? next))
        {
            events.Add(next!);
        }

        return events;
    }

    private static IEnumerable<string> Jobs(IEnumerable<SessionEvent> events) => events.Select(e => e switch
    {
        JobStarted started => $"started {started.Job.Name}",
        JobFinished finished => $"finished {finished.Job.Name}",
        JobFailed failed => $"failed {failed.Job.Name}: {failed.Error.Code}",
        JobCancelled cancelled => $"cancelled {cancelled.Job.Name}",
        _ => null,
    }).OfType<string>();

    [Fact]
    public async Task ARescan_Should_GiveTheStatus_AndWriteNothing()
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Setup();
        b.Folder.WriteFile("new.txt", "n");
        int saves = b.State.Saves;
        int writes = world.Store.Writes;

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Rescan());

        Jobs(events).ShouldBe(["started rescan", "finished rescan"]);
        FolderStatus status = events.OfType<JobFinished>().Single().Result.Status!;
        status.ShareId.ShouldBe(shareId);
        status.Entries.Single(entry => entry.Path == "new.txt").Status.ShouldBe(FileStatus.NewHere);
        b.Session.Status.ShouldBe(status);
        b.State.Saves.ShouldBe(saves);
        world.Store.Writes.ShouldBe(writes);
    }

    [Fact]
    public async Task OnlyOneJob_Should_RunAtATime()
    {
        (_, _, Machine b, _) = await Setup();

        b.Session.Rescan().IsSuccess.ShouldBeTrue();
        SharedKernel.Result second = b.Session.Sync();

        second.IsFailure.ShouldBeTrue();
        second.Error.Code.ShouldBe("Session.Busy");
        second.Error.Description.ShouldContain("rescan");
        await b.Session.WhenIdleAsync();
        b.Session.Running.ShouldBeNull();
        b.Session.Sync().IsSuccess.ShouldBeTrue();
        await b.Session.WhenIdleAsync();
    }

    [Fact]
    public async Task ASync_Should_RunTheSyncCommand_ThenRescan_WithItsEventsInOrder()
    {
        (SyncWorld world, Machine laptop, Machine b, ShareId shareId) = await Setup();
        laptop.Folder.WriteFile("from-laptop.txt", "l");
        (await laptop.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("from-b.txt", "b");

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Sync());

        Jobs(events).ShouldBe(["started sync", "finished sync", "started rescan", "finished rescan"]);
        int finishedSync = events.FindIndex(e => e is JobFinished { Job.Kind: JobKind.Sync });
        events.Take(finishedSync).OfType<PhaseStarted>().Select(phase => phase.Phase).ShouldBe([SyncPhase.Scanning, SyncPhase.Uploading, SyncPhase.Committing, SyncPhase.Downloading]);
        events.Take(finishedSync).OfType<FileFinished>().Select(file => (file.Direction, file.Path.Value))
            .ShouldBe([(TransferDirection.Upload, "from-b.txt"), (TransferDirection.Download, "from-laptop.txt")], ignoreOrder: true);
        events.OfType<FileStarted>().ShouldNotBeEmpty();
        SyncReport report = events.OfType<JobFinished>().First().Result.Sync!;
        report.Uploaded.Files.ShouldBe(1);
        b.Session.Status!.PendingChanges.ShouldBe(0);
        (await world.HeadAsync(shareId)).Entries.Keys.Select(path => path.Value).ShouldContain("from-b.txt");
    }

    [Fact]
    public async Task AFailedSync_Should_ReportTheError_AndNotRescan()
    {
        (_, Machine laptop, Machine b, _) = await Setup();
        (await laptop.DeleteAsync()).ShouldSucceed();

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Sync());

        Jobs(events).ShouldBe(["started sync", "failed sync: Shares.Deleted"]);
        b.Session.Running.ShouldBeNull();
    }

    [Fact]
    public async Task AMassDelete_Should_NeedTheExplicitFlag()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        for (int i = 0; i < 20; i++)
        {
            laptop.Folder.WriteFile($"f{i:00}.txt", $"{i}");
        }

        (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("team").Value))).ShouldSucceed();
        for (int i = 0; i < 12; i++)
        {
            laptop.Folder.Remove($"f{i:00}.txt");
        }

        List<SessionEvent> blocked = await RunAsync(laptop.Session, session => session.Sync());
        List<SessionEvent> allowed = await RunAsync(laptop.Session, session => session.Sync(allowMassDelete: true));

        Jobs(blocked).ShouldBe(["started sync", "failed sync: Sync.MassDeleteBlocked"]);
        Jobs(allowed).ShouldBe(["started sync", "finished sync", "started rescan", "finished rescan"]);
        allowed.OfType<JobFinished>().First().Result.Sync!.DeletedRemotely.ShouldBe(12);
    }

    [Theory]
    [InlineData(KeepSide.Local, "b's")]
    [InlineData(KeepSide.Remote, "laptop's")]
    public async Task Resolve_Should_KeepTheChosenSide_ThenRescan(KeepSide keep, string expected)
    {
        (_, Machine laptop, Machine b, _) = await Setup();
        laptop.Folder.WriteFile("app.yml", "laptop's");
        (await laptop.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("app.yml", "b's");
        await RunAsync(b.Session, session => session.Sync());
        b.Session.Status!.ConflictCount.ShouldBe(1);

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Resolve(["app.yml"], all: false, keep));
        (await laptop.SyncAsync()).ShouldSucceed();

        Jobs(events).ShouldBe(["started resolve", "finished resolve", "started rescan", "finished rescan"]);
        b.Session.Status!.ConflictCount.ShouldBe(0);
        b.Folder.ReadText("app.yml").ShouldBe(expected);
        laptop.Folder.ReadText("app.yml").ShouldBe(expected);
    }

    [Fact]
    public async Task Cancel_Should_StopTheJob()
    {
        (_, Machine laptop, Machine b, _) = await Setup();
        laptop.Folder.WriteFile("big.bin", "big");
        (await laptop.SyncAsync()).ShouldSucceed();
        b.Folder.OnOperation = (operation, _) =>
        {
            if (operation == MySync.Testing.FolderOperation.PlaceStagedFile)
            {
                b.Session.Cancel();
            }

            return null;
        };

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Sync());

        Jobs(events).ShouldBe(["started sync", "cancelled sync"]);
        b.Session.Running.ShouldBeNull();
        b.Folder.OnOperation = null;
        Jobs(await RunAsync(b.Session, session => session.Sync())).ShouldContain("finished sync");
    }

    /// <summary>B's disk as push sees it, matching its folder: push reads through ILocalSources.</summary>
    private static async Task<ShareId> OtherShareAndSourcesAsync(SyncWorld world, Machine b)
    {
        Machine other = world.Machine("svc@COLLECTOR");
        ShareId otherId = (await other.InitializeAsync("server-logs")).ShouldSucceed().ShareId;
        b.Sources.CurrentDirectory = "/srv/b";
        b.Sources.WriteFile("/srv/b/docs/readme.md", "# readme").WriteFile("/srv/b/docs/notes.md", "n").WriteFile("/srv/b/app.yml", "port: 80");
        return otherId;
    }

    [Theory]
    [InlineData("docs", new[] { "docs/notes.md", "docs/readme.md" })]
    [InlineData("app.yml", new[] { "app.yml" })]
    public async Task Push_Should_SendAFileOrAFolderToAnotherShare_WithoutARescan(string item, string[] landed)
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Setup();
        ShareId otherId = await OtherShareAndSourcesAsync(world, b);
        b.Folder.WriteFile("docs/notes.md", "n");
        await RunAsync(b.Session, session => session.Rescan());
        long teamRevision = (await world.HeadAsync(shareId)).Revision.Value;

        List<SessionEvent> events = await RunAsync(b.Session, session => session.Push(item, "server-logs", create: false));

        Jobs(events).ShouldBe(["started push", "finished push"]);
        MySync.Application.Sync.Push.PushReport report = events.OfType<JobFinished>().Single().Result.Push!;
        report.ShareId.ShouldBe(otherId);
        (await world.HeadAsync(otherId)).Entries.Keys.Select(path => path.Value)
            .Where(path => !path.EndsWith(Domain.Sync.Push.PushRecord.FileName, StringComparison.Ordinal))
            .ShouldBe([.. landed.Select(path => $"{report.Target}/{path}")]);
        (await world.HeadAsync(shareId)).Revision.Value.ShouldBe(teamRevision);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("{id}")]
    public async Task PushingToTheFoldersOwnShare_Should_BeRefused(string share)
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Setup();
        await OtherShareAndSourcesAsync(world, b);
        await RunAsync(b.Session, session => session.Rescan());

        SharedKernel.Result result = b.Session.Push("docs", share.Replace("{id}", shareId.ToString(), StringComparison.Ordinal), create: false);

        result.Error.Code.ShouldBe("Session.PushToOwnShare");
        result.Error.Description.ShouldBe("This folder already syncs with team. Choose another share, or use Sync.");
        b.Session.Running.ShouldBeNull();
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("/etc/passwd")]
    [InlineData("docs/../../x")]
    public async Task APathOutsideTheFolder_Should_BeRefused(string path)
    {
        (SyncWorld world, _, Machine b, _) = await Setup();
        await OtherShareAndSourcesAsync(world, b);

        SharedKernel.Result result = b.Session.Push(path, "server-logs", create: false);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
        b.Session.Running.ShouldBeNull();
    }

    [Fact]
    public async Task AnItemOnlyInTheShare_Should_HaveNothingToPush()
    {
        (SyncWorld world, Machine laptop, Machine b, _) = await Setup();
        await OtherShareAndSourcesAsync(world, b);
        laptop.Folder.WriteFile("only-in-share.txt", "s");
        (await laptop.SyncAsync()).ShouldSucceed();
        await RunAsync(b.Session, session => session.Rescan());

        b.Session.Push("only-in-share.txt", "server-logs", create: false).Error.Code.ShouldBe("Session.NothingOnDisk");
    }

    [Fact]
    public async Task ACliSyncMeanwhile_Should_BeSeenByTheNextRescan()
    {
        (_, Machine laptop, Machine b, _) = await Setup();
        laptop.Folder.WriteFile("later.txt", "l");
        (await laptop.SyncAsync()).ShouldSucceed();
        await RunAsync(b.Session, session => session.Rescan());
        b.Session.Status!.ToDownload.Files.ShouldBe(1);

        (await b.SyncAsync()).ShouldSucceed(); // the CLI, in the same folder
        await RunAsync(b.Session, session => session.Rescan());

        b.Session.Status!.PendingChanges.ShouldBe(0);
    }

    [Fact]
    public async Task Rescans_Should_ReuseTheirHashesInMemory_AndNeverWriteThem()
    {
        (_, _, Machine b, _) = await Setup();
        b.Folder.WriteFile("new.txt", "n");
        int saves = b.State.Saves;

        await RunAsync(b.Session, session => session.Rescan());
        FolderStatus first = b.Session.Status!;
        await RunAsync(b.Session, session => session.Rescan());

        first.HashCache.Keys.Select(path => path.Value).ShouldContain("new.txt");
        b.Session.Status!.HashCache.ShouldBe(first.HashCache);
        b.State.Saves.ShouldBe(saves);
        b.State.State!.HashCache.Keys.Select(path => path.Value).ShouldNotContain("new.txt");
    }

    [Fact]
    public void Progress_Should_BeCoalesced_ToAboutTenASecond()
    {
        SessionEventQueue events = new();
        ManualTime time = new();
        SessionProgress progress = new(events, time);

        progress.TransfersPlanned(TransferDirection.Upload, 1, 1000);
        for (int i = 0; i < 1000; i++)
        {
            progress.BytesTransferred(TransferDirection.Upload, 1);
        }

        time.Advance(TimeSpan.FromMilliseconds(150));
        progress.BytesTransferred(TransferDirection.Upload, 1);
        progress.FileTransferred(TransferDirection.Upload, SharePath.Create("a").Value);

        List<SessionEvent> read = [];
        while (events.TryRead(out SessionEvent? next))
        {
            read.Add(next!);
        }

        read.OfType<TransferProgress>().Select(p => p.BytesDone).ShouldBe([1001L, 1001L]);
        read.OfType<TransferProgress>().First().BytesPerSecond.ShouldBeGreaterThan(0);
        read[^1].ShouldBeOfType<FileFinished>();
    }

    [Fact]
    public void AStalledReader_Should_NeverBlockAJob_NorLoseAnImportantEvent()
    {
        SessionEventQueue events = new();
        SessionJob job = SessionJob.Sync;

        for (int i = 0; i < 100_000; i++)
        {
            events.Write(new TransferProgress(TransferDirection.Upload, i, 0));
        }

        events.Write(new JobFinished(job, new JobResult()));

        List<SessionEvent> read = [];
        while (events.TryRead(out SessionEvent? next))
        {
            read.Add(next!);
        }

        read.OfType<TransferProgress>().Count().ShouldBeLessThanOrEqualTo(4);
        read[^1].ShouldBeOfType<JobFinished>();
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.FromHours(1).Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
