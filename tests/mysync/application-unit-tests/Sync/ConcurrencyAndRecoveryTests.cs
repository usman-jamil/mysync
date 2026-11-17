using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// Races and crashes (CLAUDE.md §6, §9): another machine committing mid-sync, retries running
/// out, a crash between commit and local apply, and files edited while the sync runs.
/// </summary>
public class ConcurrencyAndRecoveryTests
{
    [Fact]
    public async Task AnotherMachineCommittingMidSync_Should_BeMergedOnRetry_WithNothingLost()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("a.txt", "1"), ("b.txt", "1"));
        a.Folder.WriteFile("a.txt", "a's edit");
        b.Folder.WriteFile("b.txt", "b's edit");

        // b's sync runs to completion between a's read of HEAD and a's write.
        bool fired = false;
        world.Store.BeforeReplaceManifest = async _ =>
        {
            if (!fired)
            {
                fired = true;
                (await b.SyncAsync()).ShouldSucceed();
            }
        };

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Revision.ShouldBe(Rev(3)); // b committed 2, a re-planned and committed 3
        a.Files().ShouldBe(new Dictionary<string, string> { ["a.txt"] = "a's edit", ["b.txt"] = "b's edit" });
        Manifest head = await world.HeadAsync(shareId);
        head.UpdatedBy.ShouldBe("usman@DEV-LAPTOP");
        head.Entries.Values.OfType<FileEntry>().Select(file => file.Sha256)
            .ShouldBe([ManifestBuilder.HashOf("a's edit"), ManifestBuilder.HashOf("b's edit")], ignoreOrder: true);
        world.Time.Delays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task LosingTheRaceEveryTime_Should_GiveUpAfterFiveAttempts_WithoutChangingTheFolderState()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "1"));
        a.Folder.WriteFile("a.txt", "2");
        ShareStateSnapshot before = new(a);
        world.Store.BeforeReplaceManifest = world.CommitElsewhereAsync;

        (await a.SyncAsync()).ShouldFail().ShouldBe(SyncErrors.CommitRetriesExhausted(5));

        new ShareStateSnapshot(a).ShouldBe(before);
        (await world.HeadAsync(shareId)).UpdatedBy.ShouldBe("other@ELSEWHERE");
    }

    [Fact]
    public async Task RetryBackOff_Should_DoubleEachTime_WithJitter()
    {
        (SyncWorld world, Machine a, _, _) = await TwoMachines(("a.txt", "1"));
        a.Folder.WriteFile("a.txt", "2");
        world.Store.BeforeReplaceManifest = world.CommitElsewhereAsync;

        await a.SyncAsync();

        IReadOnlyList<TimeSpan> delays = world.Time.Delays;
        delays.Count.ShouldBe(4); // between five attempts
        for (int i = 0; i < delays.Count; i++)
        {
            double expected = 200 * Math.Pow(2, i);
            delays[i].TotalMilliseconds.ShouldBeInRange(expected * 0.5, expected * 1.5);
        }
    }

    [Fact]
    public async Task ACrashBetweenCommitAndApply_Should_BeRepairedByTheNextRun_WithoutCommittingAgain()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("from-a.txt", "1"), ("from-b.txt", "1"));
        a.Folder.WriteFile("from-a.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("from-b.txt", "b's edit");
        b.Folder.OnOperation = (operation, path) =>
            operation == FolderOperation.PlaceStagedFile ? LocalFileErrors.WriteFailed(path.Value, "disk full") : null;

        SyncReport failed = (await b.SyncAsync()).ShouldSucceed();

        failed.Committed.ShouldBeTrue(); // b's own change went up...
        failed.Skipped.Single().Cause.ShouldBe(SkipCause.ApplyFailed); // ...but a's didn't come down
        b.Folder.ReadText("from-a.txt").ShouldBe("1");
        b.State.State!.Base[ManifestBuilder.PathOf("from-a.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("1"));
        b.Folder.StagedFileCount.ShouldBe(0);
        Revision afterCrash = (await world.HeadAsync(shareId)).Revision;

        b.Folder.OnOperation = null;
        SyncReport repaired = (await b.SyncAsync()).ShouldSucceed();

        repaired.Committed.ShouldBeFalse();
        repaired.Downloaded.Files.ShouldBe(1);
        b.Folder.ReadText("from-a.txt").ShouldBe("a's edit");
        (await world.HeadAsync(shareId)).Revision.ShouldBe(afterCrash);
    }

    [Fact]
    public async Task AFileEditedAfterTheScan_Should_NotBeOverwritten_AndBecomeAConflictNextRun()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("x.txt", "base"));
        a.Folder.WriteFile("x.txt", "a's edit");
        (await a.SyncAsync()).ShouldSucceed();

        // The user saves x.txt on b while b's sync is between its scan and its download.
        b.Folder.OnOperation = (operation, path) =>
        {
            if (operation == FolderOperation.GetFile && path.Value == "x.txt")
            {
                b.Folder.OnOperation = null;
                b.Folder.WriteFile("x.txt", "b saved this just now", mtimeUtc: DateTimeOffset.UnixEpoch.AddYears(60));
            }

            return null;
        };

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();

        report.Skipped.Single().ShouldBe(new SkippedPath("x.txt", SkipCause.ChangedSinceScan, "it changed after the scan"));
        b.Folder.ReadText("x.txt").ShouldBe("b saved this just now");

        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);
    }

    [Fact]
    public async Task AFileChangingDuringUpload_Should_BeLeftOutOfTheCommit_AndRetriedNextRun()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("log.txt", "v1"), ("doc.txt", "v1"));
        a.Folder.WriteFile("log.txt", "log v2").WriteFile("doc.txt", "doc v2");
        a.Folder.OnOperation = (operation, path) =>
        {
            if (operation == FolderOperation.OpenRead && path.Value == "log.txt")
            {
                a.Folder.OnOperation = null;
                a.Folder.WriteFile("log.txt", "v3, appended during the upload");
            }

            return null;
        };

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Skipped.Single().Cause.ShouldBe(SkipCause.Unstable);
        Manifest head = await world.HeadAsync(shareId);
        head.Entries[ManifestBuilder.PathOf("log.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("v1"));
        head.Entries[ManifestBuilder.PathOf("doc.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("doc v2"));

        (await a.SyncAsync()).ShouldSucceed();
        (await world.HeadAsync(shareId)).Entries[ManifestBuilder.PathOf("log.txt")].ShouldBeOfType<FileEntry>()
            .Sha256.ShouldBe(ManifestBuilder.HashOf("v3, appended during the upload"));
    }

    [Fact]
    public async Task AFileTheScannerFoundUnstable_Should_BeLeftAlone_NotTakenAsDeleted()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("busy.db", "v1"));
        b.Folder.WriteFile("busy.db", "b's version");
        (await b.SyncAsync()).ShouldSucceed();
        a.Folder.UnstablePaths.Add("busy.db");

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeFalse();
        report.Conflicts.ShouldBeEmpty();
        report.Plan.Actions.Single().SkipReason.ShouldBe(SkipReason.Deferred);
        report.Skipped.Single().Cause.ShouldBe(SkipCause.Unstable);
        a.Folder.ReadText("busy.db").ShouldBe("v1"); // not overwritten while it is busy
        (await world.HeadAsync(shareId)).Entries.ContainsKey(ManifestBuilder.PathOf("busy.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileTheScannerFoundUnstable_Should_NotBeDeletedRemotely_WhenTheRemoteIsUnchanged()
    {
        // The scan leaves an unstable file out. Taken literally, "absent locally, unchanged
        // remotely" would mean "deleted here", and the file would be removed from the share.
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("busy.db", "v1"), ("other.txt", "o"));
        a.Folder.UnstablePaths.Add("busy.db");

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeFalse();
        report.DeletedRemotely.ShouldBe(0);
        (await world.HeadAsync(shareId)).Entries.ContainsKey(ManifestBuilder.PathOf("busy.db")).ShouldBeTrue();
        a.State.State!.Base.ContainsKey(ManifestBuilder.PathOf("busy.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task ADirectoryTheScannerSkipped_Should_NotHaveItsContentsDeletedRemotely_OrOverwritten()
    {
        // An unreadable directory (or one replaced by a symlink) hides everything under it.
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("private/a.txt", "a"), ("private/sub/b.txt", "b"), ("other.txt", "o"));
        b.Folder.WriteFile("private/a.txt", "b's edit");
        (await b.SyncAsync()).ShouldSucceed();
        a.Folder.UnstablePaths.Add("private");

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Committed.ShouldBeFalse();
        report.DeletedRemotely.ShouldBe(0);
        report.Downloaded.Files.ShouldBe(0);
        a.Folder.ReadText("private/a.txt").ShouldBe("a");
        (await world.HeadAsync(shareId)).Entries.Keys.Select(path => path.Value)
            .ShouldBe(["other.txt", "private/a.txt", "private/sub/b.txt"]);
        a.State.State!.Base[ManifestBuilder.PathOf("private/a.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a"));
    }

    [Fact]
    public async Task ADownloadThatFailsMidStream_Should_SkipThatPath_AndApplyTheRest()
    {
        (SyncWorld world, Machine a, Machine b, _) = await TwoMachines(("flaky.bin", "v1"), ("fine.txt", "v1"));
        b.Folder.WriteFile("flaky.bin", "b's long new version").WriteFile("fine.txt", "b's edit");
        (await b.SyncAsync()).ShouldSucceed();
        ContentHash flaky = ManifestBuilder.HashOf("b's long new version");
        world.Store.DownloadStream = (hash, bytes) => hash == flaky ? new FailingStream(bytes, failAfter: 5) : new MemoryStream(bytes);

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Skipped.Single().ShouldBe(report.Skipped.Single() with { Path = "flaky.bin", Cause = SkipCause.ApplyFailed });
        a.Folder.ReadText("flaky.bin").ShouldBe("v1");
        a.Folder.ReadText("fine.txt").ShouldBe("b's edit");
        a.Folder.StagedFileCount.ShouldBe(0);

        world.Store.DownloadStream = null;
        (await a.SyncAsync()).ShouldSucceed().Downloaded.Files.ShouldBe(1);
        a.Folder.ReadText("flaky.bin").ShouldBe("b's long new version");
    }

    [Fact]
    public async Task AnUploadWhoseFileFailsMidRead_Should_SkipThatPath_AndCommitTheRest()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("locked.db", "v1"), ("fine.txt", "v1"));
        a.Folder.WriteFile("locked.db", "a's long new version").WriteFile("fine.txt", "a's edit");
        a.Folder.ReadStream = (path, bytes) => path.Value == "locked.db" ? new FailingStream(bytes, failAfter: 5) : new MemoryStream(bytes);

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Skipped.Single().ShouldBe(report.Skipped.Single() with { Path = "locked.db", Cause = SkipCause.Unreadable });
        Manifest head = await world.HeadAsync(shareId);
        head.Entries[ManifestBuilder.PathOf("locked.db")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("v1"));
        head.Entries[ManifestBuilder.PathOf("fine.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("a's edit"));
    }

    [Fact]
    public async Task Progress_Should_ReportPhasesPlansBytesAndFiles_InOrder()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("up.txt", "1"));
        b.Folder.WriteFile("down.txt", "from b");
        (await b.SyncAsync()).ShouldSucceed();
        a.Folder.WriteFile("up.txt", "a's edit");
        RecordingSyncProgressReporter progress = new();

        (await a.SyncAsync(new RunSyncCommand(Progress: progress))).ShouldSucceed();

        progress.Lines.ShouldBe([
            "phase Scanning",
            "phase Uploading",
            "planned Upload 1 files 8 bytes",
            "bytes Upload 8",
            "file Upload up.txt",
            "phase Committing",
            "phase Downloading",
            "planned Download 1 files 6 bytes",
            "bytes Download 6",
            "file Download down.txt",
        ]);
    }

    [Fact]
    public async Task ParallelTransfers_Should_GiveTheSameResult()
    {
        (_, Machine a, Machine b, _) = await TwoMachines([.. Enumerable.Range(0, 40).Select(i => ($"f{i:D2}.txt", $"v1 {i}"))]);
        foreach (int i in Enumerable.Range(0, 40))
        {
            a.Folder.WriteFile($"f{i:D2}.txt", $"v2 {i}");
        }

        (await a.SyncAsync(new RunSyncCommand(Parallelism: 8))).ShouldSucceed().Uploaded.Files.ShouldBe(40);
        (await b.SyncAsync(new RunSyncCommand(Parallelism: 8))).ShouldSucceed().Downloaded.Files.ShouldBe(40);

        b.Files().ShouldBe(a.Files());
    }

    // What the folder's saved state says, for "nothing changed" checks.
    private sealed record ShareStateSnapshot(long BaseRevision, int Saves, string BaseHashes)
    {
        public ShareStateSnapshot(Machine machine)
            : this(
                machine.State.State!.BaseRevision.Value,
                machine.State.Saves,
                string.Join(",", machine.State.State.Base.Values.OfType<FileEntry>().Select(file => file.Sha256.Value)))
        {
        }
    }
}
