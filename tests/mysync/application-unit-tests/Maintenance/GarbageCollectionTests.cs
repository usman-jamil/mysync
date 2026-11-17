using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Content;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Maintenance;

/// <summary><c>mysync gc</c> (CLAUDE.md §10): delete only what nothing kept refers to, and only once it is old enough.</summary>
public class GarbageCollectionTests
{
    private static readonly ContentHash V1 = ManifestBuilder.HashOf("v1");
    private static readonly ContentHash V2 = ManifestBuilder.HashOf("v2");
    private static readonly ContentHash V3 = ManifestBuilder.HashOf("v3");
    private static readonly ContentHash Stable = ManifestBuilder.HashOf("stable");

    [Fact]
    public async Task Gc_Should_DeleteObjectsOnlyOldRevisionsUsed_AndPruneTheirHistory()
    {
        (SyncWorld world, Machine a, ShareId shareId) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));

        GarbageReport report = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1))).ShouldSucceed();

        report.DeletedObjects.Select(item => item.Hash).ShouldBe([V1, V2], ignoreOrder: true);
        report.DeletedHistory.Select(revision => revision.Value).ShouldBe([1, 2]);
        report.KeptRevisions.Select(revision => revision.Value).ShouldBe([3]);
        report.KeptObjects.ShouldBe(2);
        Objects(world, shareId).ShouldBe([V3, Stable], ignoreOrder: true);
        History(world, shareId).ShouldBe([3]);
        (await world.HeadAsync(shareId)).Revision.ShouldBe(Rev(3));
    }

    [Fact]
    public async Task Gc_Should_KeepWhatTheKeptHistoryReferences()
    {
        (SyncWorld world, Machine a, ShareId shareId) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));

        GarbageReport report = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 2))).ShouldSucceed();

        report.DeletedObjects.Select(item => item.Hash).ShouldBe([V1]);
        report.DeletedHistory.Select(revision => revision.Value).ShouldBe([1]);
        Objects(world, shareId).ShouldBe([V2, V3, Stable], ignoreOrder: true);
    }

    [Fact]
    public async Task Gc_Should_LeaveUnreferencedObjectsAlone_UntilTheGracePeriodHasPassed()
    {
        (SyncWorld world, Machine a, ShareId shareId) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(23));

        GarbageReport report = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1))).ShouldSucceed();

        report.DeletedObjects.ShouldBeEmpty();
        report.ProtectedByGrace.ShouldBe(2);
        Objects(world, shareId).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Gc_Should_ProtectAnObjectUploadedForACommitNotMadeYet()
    {
        (SyncWorld world, Machine a, ShareId shareId) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));
        // Another machine has uploaded this but not committed it yet.
        ContentHash pending = ManifestBuilder.HashOf("pending");
        await world.Store.UploadObjectAsync(shareId, pending, new MemoryStream("pending"u8.ToArray()), 7, SyncWorld.Ct);

        GarbageReport report = (await a.GcAsync()).ShouldSucceed();

        report.DeletedObjects.Select(item => item.Hash).ShouldNotContain(pending);
        Objects(world, shareId).ShouldContain(pending);
    }

    [Fact]
    public async Task DryRun_Should_ReportTheSame_AndDeleteNothing()
    {
        (SyncWorld world, Machine a, _) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));
        IReadOnlyList<string> blobs = world.Store.BlobNames;

        GarbageReport report = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1, DryRun: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.DeletedObjects.Length.ShouldBe(2);
        report.DeletedBytes.ShouldBe(4);
        report.DeletedHistory.Length.ShouldBe(2);
        world.Store.BlobNames.ShouldBe(blobs);
    }

    [Fact]
    public async Task AShareThatMovesWhileGcPlans_Should_StopIt_BeforeItDeletesAnything()
    {
        (SyncWorld world, Machine a, _) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));
        world.Store.BeforeListObjects = world.CommitElsewhereAsync;
        IReadOnlyList<string> objects = [.. world.Store.BlobNames.Where(name => name.Contains("/objects/", StringComparison.Ordinal))];

        Error error = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1))).ShouldFail();

        error.ShouldBe(Domain.Sync.SyncErrors.ShareChangedDuringGc(3, 4));
        world.Store.BlobNames.Where(name => name.Contains("/objects/", StringComparison.Ordinal)).ShouldBe(objects);
    }

    [Fact]
    public async Task AnUnreadableHistoryManifest_Should_StopGc_BeforeItDeletesAnything()
    {
        (SyncWorld world, Machine a, _) = await ThreeRevisions();
        world.Clock.Advance(TimeSpan.FromHours(25));
        world.Store.UnreadableHistory.Add(2);
        IReadOnlyList<string> blobs = world.Store.BlobNames;

        (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1))).ShouldFail().Code.ShouldBe("Shares.InvalidManifest");

        world.Store.BlobNames.ShouldBe(blobs);
    }

    [Fact]
    public async Task NothingToCollect_Should_Succeed_AndDeleteNothing()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "a"));
        world.Clock.Advance(TimeSpan.FromDays(30));

        GarbageReport report = (await a.GcAsync()).ShouldSucceed();

        report.DeletedObjects.ShouldBeEmpty();
        report.DeletedHistory.ShouldBeEmpty();
        Objects(world, shareId).ShouldBe([ManifestBuilder.HashOf("a")]);
    }

    [Theory]
    [InlineData(0, 24, "Maintenance.InvalidKeepRevisions")]
    [InlineData(20, 0, "Maintenance.InvalidGraceHours")]
    [InlineData(20, -5, "Maintenance.InvalidGraceHours")]
    public async Task InvalidOptions_Should_BeRejected(int keepRevisions, int graceHours, string code)
    {
        (_, Machine a, _, _) = await TwoMachines(("a.txt", "a"));

        Error error = (await a.GcAsync(new CollectGarbageCommand(keepRevisions, graceHours))).ShouldFail();

        error.ShouldBeOfType<ValidationError>().Errors.Single().Code.ShouldBe(code);
    }

    // Revision 1: doc = v1; 2: doc = v2; 3: doc = v3. stable.txt never changes.
    private static async Task<(SyncWorld World, Machine A, ShareId ShareId)> ThreeRevisions()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("doc.txt", "v1"), ("stable.txt", "stable"));
        a.Folder.WriteFile("doc.txt", "v2");
        (await a.SyncAsync()).ShouldSucceed();
        a.Folder.WriteFile("doc.txt", "v3");
        (await a.SyncAsync()).ShouldSucceed();
        return (world, a, shareId);
    }

    private static List<ContentHash> Objects(SyncWorld world, ShareId shareId)
    {
        var layout = ShareLayout.For(shareId);
        return [.. world.Store.BlobNames.Select(name => layout.TryParseObject(name, out ContentHash? hash) ? hash : null).OfType<ContentHash>()];
    }

    private static List<long> History(SyncWorld world, ShareId shareId)
    {
        var layout = ShareLayout.For(shareId);
        return [.. world.Store.BlobNames.Select(name => layout.TryParseManifestHistory(name, out Domain.Manifests.Revision? revision) ? revision.Value : 0).Where(value => value > 0).Order()];
    }
}
