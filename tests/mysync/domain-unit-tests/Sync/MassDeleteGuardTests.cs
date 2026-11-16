using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Sync;

/// <summary>
/// The guard blocks when a plan deletes more than half and more than 10 of the local files, or
/// of the remote entries (CLAUDE.md §9 phase 5). The cases sit at and around both thresholds.
/// </summary>
public class MassDeleteGuardTests
{
    [Theory]
    [InlineData(11, 21, true)] // 52%, 11 > 10
    [InlineData(11, 22, false)] // exactly 50% is not more than half
    [InlineData(12, 22, true)]
    [InlineData(10, 10, false)] // 100%, but only 10
    [InlineData(11, 11, true)]
    [InlineData(10, 19, false)]
    [InlineData(0, 0, false)]
    [InlineData(500, 1000, false)]
    [InlineData(501, 1000, true)]
    public void LocalDeletes_Should_BeBlockedOnlyAboveBothThresholds(int deleted, int total, bool blocked)
    {
        SyncPlan plan = RemoteDeletedFiles(deleted, total);

        plan.LocalFileDeletes.ShouldBe(deleted);
        MassDeleteGuard.Check(plan, allowMassDelete: false).IsFailure.ShouldBe(blocked);
    }

    [Theory]
    [InlineData(11, 21, true)]
    [InlineData(11, 22, false)]
    [InlineData(10, 10, false)]
    public void RemoteDeletes_Should_BeBlockedOnlyAboveBothThresholds(int deleted, int total, bool blocked)
    {
        SyncPlan plan = LocalDeletedFiles(deleted, total);

        plan.RemoteEntryDeletes.ShouldBe(deleted);
        MassDeleteGuard.Check(plan, allowMassDelete: false).IsFailure.ShouldBe(blocked);
    }

    [Fact]
    public void Error_Should_SayHowManyOfHowMany()
    {
        SharedKernel.Error error = MassDeleteGuard.Check(RemoteDeletedFiles(15, 20), allowMassDelete: false).Error;

        error.ShouldBe(SyncErrors.MassDeleteBlocked(15, 20));
        error.Description.ShouldContain("--allow-mass-delete");
    }

    [Fact]
    public void AllowMassDelete_Should_LetAnyPlanThrough()
    {
        MassDeleteGuard.Check(RemoteDeletedFiles(20, 20), allowMassDelete: true).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void NoDelete_Should_NeverTripTheGuard_BecauseItDeletesNothing()
    {
        SyncPlan plan = RemoteDeletedFiles(20, 20, new MergeOptions(NoDelete: true));

        plan.LocalFileDeletes.ShouldBe(0);
        MassDeleteGuard.Check(plan, allowMassDelete: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void EmptyDirectories_Should_NotCountAsLocalFiles()
    {
        // 11 files deleted out of 11 files, plus 100 untouched empty directories: still a mass delete.
        Dictionary<SharePath, ManifestEntry> tree = Files(11);
        foreach (int i in Enumerable.Range(0, 100))
        {
            tree[Path($"dir{i}")] = DirectoryEntry.Instance;
        }

        var remote = tree.Where(pair => pair.Value is DirectoryEntry).ToDictionary();
        SyncPlan plan = MergePlanner.Plan(tree, tree, remote);

        plan.LocalFileCount.ShouldBe(11);
        MassDeleteGuard.Check(plan, allowMassDelete: false).IsFailure.ShouldBeTrue();
    }

    // Base == local == `total` files; the remote lost the first `deleted` of them → local deletes.
    private static SyncPlan RemoteDeletedFiles(int deleted, int total, MergeOptions? options = null)
    {
        Dictionary<SharePath, ManifestEntry> all = Files(total);
        return MergePlanner.Plan(all, all, all.Skip(deleted).ToDictionary(), options);
    }

    // Base == remote == `total` files; the local folder lost `deleted` of them → remote deletes.
    private static SyncPlan LocalDeletedFiles(int deleted, int total)
    {
        Dictionary<SharePath, ManifestEntry> all = Files(total);
        return MergePlanner.Plan(all, all.Skip(deleted).ToDictionary(), all);
    }

    private static Dictionary<SharePath, ManifestEntry> Files(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => Path($"f{i:D4}"), _ => (ManifestEntry)File());
}
