using MySync.Application.Session;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Session;

/// <summary>
/// <c>mysync tui</c>'s file statuses (CLAUDE.md §10d): every merge-plan action maps to exactly one
/// status, and <see cref="GetFolderStatusQuery"/> is <c>status</c>'s preview, writing nothing.
/// </summary>
public class FolderStatusTests
{
    private static readonly FileEntry H1 = new ManifestBuilder().WithFile("x", "one").Entries[ManifestBuilder.PathOf("x")] as FileEntry ?? throw new InvalidOperationException();
    private static readonly FileEntry H2 = new ManifestBuilder().WithFile("x", "two").Entries[ManifestBuilder.PathOf("x")] as FileEntry ?? throw new InvalidOperationException();

    public static TheoryData<SyncActionKind> Kinds() => [.. Enum.GetValues<SyncActionKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryPlanAction_Should_MapToOneStatus_WhateverItsSides(SyncActionKind kind)
    {
        ManifestEntry?[] sides = [null, H1, H2, DirectoryEntry.Instance];
        foreach (ManifestEntry? @base in sides)
        {
            foreach (ManifestEntry? local in sides)
            {
                foreach (ManifestEntry? remote in sides)
                {
                    SyncAction action = new(ManifestBuilder.PathOf("x"), kind, @base, local, remote);
                    Enum.IsDefined(FolderStatusMapper.StatusOf(action, skip: null)).ShouldBeTrue();
                }
            }
        }
    }

    [Theory]
    [InlineData(SyncActionKind.NoChange, true, FileStatus.Synced)]
    [InlineData(SyncActionKind.Upload, false, FileStatus.NewHere)]
    [InlineData(SyncActionKind.Upload, true, FileStatus.ChangedHere)]
    [InlineData(SyncActionKind.UpdateRemoteMode, true, FileStatus.ChangedHere)]
    [InlineData(SyncActionKind.AddRemoteDirectory, false, FileStatus.NewHere)]
    [InlineData(SyncActionKind.RemoveRemote, true, FileStatus.DeletedHere)]
    [InlineData(SyncActionKind.Download, false, FileStatus.NewInShare)]
    [InlineData(SyncActionKind.Download, true, FileStatus.ChangedInShare)]
    [InlineData(SyncActionKind.UpdateLocalMode, true, FileStatus.ChangedInShare)]
    [InlineData(SyncActionKind.CreateLocalDirectory, false, FileStatus.NewInShare)]
    [InlineData(SyncActionKind.DeleteLocal, true, FileStatus.DeletedInShare)]
    [InlineData(SyncActionKind.Conflict, true, FileStatus.Conflict)]
    [InlineData(SyncActionKind.Skip, true, FileStatus.Skipped)]
    public void EachKind_Should_MapAsTheTableSays(SyncActionKind kind, bool hasBase, FileStatus expected)
    {
        ManifestEntry? @base = hasBase ? H1 : null;
        SyncAction action = new(ManifestBuilder.PathOf("x"), kind, @base, H2, H2);

        FolderStatusMapper.StatusOf(action, skip: null).ShouldBe(expected);
    }

    [Fact]
    public void ASkipTheIgnoreRulesCaused_Should_BeIgnored()
    {
        SyncAction action = new(ManifestBuilder.PathOf("x"), SyncActionKind.Skip, null, null, H1, SkipReason: SkipReason.Deferred);

        FolderStatusMapper.StatusOf(action, new SkippedPath("x", SkipCause.IgnoredLocally, "ignored here")).ShouldBe(FileStatus.Ignored);
        FolderStatusMapper.StatusOf(action, new SkippedPath("x", SkipCause.Unstable, "busy")).ShouldBe(FileStatus.Skipped);
    }

    /// <summary>A share with every kind of difference between this machine (B) and the share.</summary>
    private static async Task<(SyncWorld World, Machine B)> EveryStatusAsync()
    {
        SyncWorld world = new();
        Machine a = world.Machine("usman@DEV-LAPTOP");
        a.Folder.WriteFile("synced.txt", "s").WriteFile("edit-here.txt", "1").WriteFile("delete-here.txt", "d")
            .WriteFile("edit-share.txt", "1").WriteFile("delete-share.txt", "d").WriteFile("both.txt", "base");
        (await a.CreateAsync(new CreateShareCommand(ShareName.Create("team").Value))).ShouldSucceed();
        Machine b = world.Machine("ali@SERVER01");
        (await b.CloneAsync("team")).ShouldSucceed();

        a.Folder.WriteFile("edit-share.txt", "2").WriteFile("new-share.txt", "n").WriteFile("both.txt", "a's").WriteFile("ignored.log", "i");
        a.Folder.Remove("delete-share.txt");
        (await a.SyncAsync()).ShouldSucceed();

        b.Folder.WriteFile("edit-here.txt", "2").WriteFile("new-here.txt", "n").WriteFile("both.txt", "b's").WriteFile("busy.txt", "b");
        b.Folder.Remove("delete-here.txt");
        b.Folder.UnstablePaths.Add("busy.txt");
        b.IgnoreRules.Matcher = new SimplePatternIgnoreMatcher("*.log\n");
        return (world, b);
    }

    [Fact]
    public async Task TheQuery_Should_GiveEveryPathItsStatus()
    {
        (SyncWorld world, Machine b) = await EveryStatusAsync();

        FolderStatus status = (await b.FolderStatusAsync()).ShouldSucceed();

        status.Entries.ToDictionary(entry => entry.Path, entry => entry.Status).ShouldBe(new Dictionary<string, FileStatus>
        {
            ["both.txt"] = FileStatus.Conflict,
            ["busy.txt"] = FileStatus.Skipped,
            ["delete-here.txt"] = FileStatus.DeletedHere,
            ["delete-share.txt"] = FileStatus.DeletedInShare,
            ["edit-here.txt"] = FileStatus.ChangedHere,
            ["edit-share.txt"] = FileStatus.ChangedInShare,
            ["ignored.log"] = FileStatus.Ignored,
            ["new-here.txt"] = FileStatus.NewHere,
            ["new-share.txt"] = FileStatus.NewInShare,
            ["synced.txt"] = FileStatus.Synced,
        });
        status.Names.ShouldBe([ShareName.Create("team").Value]);
        status.ShareLabel.ShouldBe("team");
        status.HeadRevision.Value.ShouldBe(2);
        status.HeadUpdatedBy.ShouldBe("usman@DEV-LAPTOP");
        status.ToUpload.ShouldBe(new PendingTotals(2, 2));
        status.ToDownload.ShouldBe(new PendingTotals(2, 2));
        status.ToDelete.ShouldBe(2);
        status.ConflictCount.ShouldBe(1);
        status.SkippedCount.ShouldBe(1);
        status.PendingChanges.ShouldBe(6);

        FolderEntry share = status.Entries.Single(entry => entry.Path == "edit-share.txt");
        (share.ChangedBy, share.ChangedAt!.Value, share.Size).ShouldBe(("usman@DEV-LAPTOP", 2L, (long?)1));
        FolderEntry conflict = status.Entries.Single(entry => entry.Path == "both.txt");
        conflict.ConflictKind.ShouldBe(ConflictKind.ModifyModify);
        conflict.ChangedBy.ShouldBe("usman@DEV-LAPTOP");
        status.Entries.Single(entry => entry.Path == "busy.txt").Detail.ShouldNotBeNullOrEmpty();
        status.Entries.Single(entry => entry.Path == "new-share.txt").ExistsHere.ShouldBeFalse();
        status.Entries.Single(entry => entry.Path == "delete-share.txt").ExistsHere.ShouldBeTrue();
        _ = world;
    }

    [Fact]
    public async Task TheQuery_Should_WriteNothing()
    {
        (SyncWorld world, Machine b) = await EveryStatusAsync();
        int saves = b.State.Saves;
        int writes = world.Store.Writes;
        int locks = b.Lock.Acquisitions;
        int cleans = b.Folder.StagingCleans;
        int logLines = b.ConflictLog.Lines.Count;

        (await b.FolderStatusAsync()).ShouldSucceed();
        (await b.FolderStatusAsync()).ShouldSucceed();

        b.State.Saves.ShouldBe(saves);
        world.Store.Writes.ShouldBe(writes);
        b.Lock.Acquisitions.ShouldBe(locks);
        b.Folder.StagingCleans.ShouldBe(cleans);
        b.ConflictLog.Lines.Count.ShouldBe(logLines);
    }

    [Fact]
    public async Task TheQuery_Should_HandBackItsHashes_AndTakeTheCallersOnes_WithoutWritingThem()
    {
        (_, Machine b) = await EveryStatusAsync();
        int saves = b.State.Saves;

        FolderStatus first = (await b.FolderStatusAsync()).ShouldSucceed();
        FolderStatus second = (await b.FolderStatusAsync(new GetFolderStatusQuery(first.HashCache))).ShouldSucceed();

        first.HashCache.Keys.Select(path => path.Value).ShouldContain("new-here.txt");
        second.HashCache.ShouldBe(first.HashCache);
        b.State.State!.HashCache.Keys.Select(path => path.Value).ShouldNotContain("new-here.txt");
        b.State.Saves.ShouldBe(saves);
    }

    [Fact]
    public async Task OutsideAShare_Should_FailAsStatusDoes()
    {
        SyncWorld world = new();
        Machine stranger = world.Machine("x@Y");

        (await stranger.FolderStatusAsync()).ShouldFail().Code.ShouldBe("Shares.NotAShare");
    }
}
