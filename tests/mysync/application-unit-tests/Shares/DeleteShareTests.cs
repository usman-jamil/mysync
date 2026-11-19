using MySync.Application.Shares.Delete;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Shares;

/// <summary><c>mysync delete</c>: the share goes, the marker stays, and every other machine is told.</summary>
public class DeleteShareTests
{
    [Fact]
    public async Task Delete_Should_LeaveOnlyTheMarker_InTheStore()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("secret.txt", "s3cr3t"), ("docs/plan.md", "plan"));
        a.Folder.WriteFile("secret.txt", "edited");
        (await a.SyncAsync()).ShouldSucceed();

        DeleteShareResponse deleted = (await a.DeleteAsync()).ShouldSucceed();

        deleted.WasAlreadyDeleted.ShouldBeFalse();
        deleted.MarkerRevision.ShouldBe(Rev(3));
        deleted.ObjectsDeleted.ShouldBe(3);
        deleted.Deletion!.DeletedBy.ShouldBe("usman@DEV-LAPTOP");
        world.Store.BlobNames.ShouldBe([ShareLayout.For(shareId).Manifest]);
        Manifest head = await world.HeadAsync(shareId);
        head.IsDeleted.ShouldBeTrue();
        head.Entries.ShouldBeEmpty();
        head.Revision.ShouldBe(Rev(3));
    }

    [Fact]
    public async Task Delete_Should_KeepThisFoldersFiles_AndTurnItIntoAnOrdinaryFolder()
    {
        (_, Machine a, _, _) = await TwoMachines(("secret.txt", "s3cr3t"));

        DeleteShareResponse deleted = (await a.DeleteAsync()).ShouldSucceed();

        deleted.LocalStateRemoved.ShouldBeTrue();
        a.Folder.ReadText("secret.txt").ShouldBe("s3cr3t");
        a.State.State.ShouldBeNull();
        a.ConflictLog.Deleted.ShouldBeTrue();
        a.Lock.IsHeld.ShouldBeFalse();
        a.Lock.Removed.ShouldBeTrue();
    }

    [Fact]
    public async Task OtherMachines_Should_BeToldTheShareWasDeleted_AndKeepTheirFiles()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("secret.txt", "s3cr3t"));
        b.Folder.WriteFile("secret.txt", "b's unsynced edit");
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        (await a.DeleteAsync()).ShouldSucceed();
        int saves = b.State.Saves;

        Error error = (await b.SyncAsync()).ShouldFail();

        error.Code.ShouldBe("Shares.Deleted");
        error.Type.ShouldBe(ErrorType.NotFound);
        error.Description.ShouldContain("deleted by usman@DEV-LAPTOP on 2026-11-16 09:05 UTC");
        b.Folder.ReadText("secret.txt").ShouldBe("b's unsynced edit");
        b.State.Saves.ShouldBe(saves);
        (await b.StatusAsync()).ShouldFail().Code.ShouldBe("Shares.Deleted");
        (await b.InfoAsync()).ShouldFail().Code.ShouldBe("Shares.Deleted");
        (await b.GcAsync()).ShouldFail().Code.ShouldBe("Shares.Deleted");
        (await world.Machine("c").CloneAsync(shareId)).ShouldFail().Code.ShouldBe("Shares.Deleted");
    }

    [Fact]
    public async Task DeletingByIdFromAnotherFolder_Should_DeleteTheShare_AndLeaveThatFolderAlone()
    {
        (SyncWorld world, _, _, ShareId shareId) = await TwoMachines(("a.txt", "a"));
        Machine admin = world.Machine("admin@OPS");
        admin.Folder.WriteFile("unrelated.txt", "mine");

        DeleteShareResponse deleted = (await admin.DeleteAsync(shareId.ToString())).ShouldSucceed();

        deleted.LocalStateRemoved.ShouldBeFalse();
        (await world.HeadAsync(shareId)).IsDeleted.ShouldBeTrue();
        admin.Folder.ReadText("unrelated.txt").ShouldBe("mine");
        admin.Lock.Acquisitions.ShouldBe(0);
    }

    [Fact]
    public async Task DeletingAnotherShareById_Should_NotTouchThisFoldersOwnShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        ShareId mine = (await a.WriteAndCreate("mine.txt", "m")).ShouldSucceed().ShareId;
        Machine b = world.Machine("b");
        ShareId theirs = (await b.WriteAndCreate("theirs.txt", "t")).ShouldSucceed().ShareId;

        (await a.DeleteAsync(theirs.ToString())).ShouldSucceed().LocalStateRemoved.ShouldBeFalse();

        a.State.State!.ShareId.ShouldBe(mine);
        (await a.SyncAsync()).ShouldSucceed();
        (await world.HeadAsync(mine)).IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task AnInterruptedDelete_Should_BeFinishedByRunningItAgain()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("one.txt", "1"), ("two.txt", "2"), ("three.txt", "3"));
        int deletes = 0;
        world.Store.BeforeDeleteObject = _ => ++deletes == 2 ? StorageErrors.Unreachable : null;

        (await a.DeleteAsync()).ShouldFail().ShouldBe(StorageErrors.Unreachable);

        (await world.HeadAsync(shareId)).IsDeleted.ShouldBeTrue(); // the marker went first
        a.State.State.ShouldNotBeNull(); // nothing local was removed yet
        world.Store.BeforeDeleteObject = null;

        DeleteShareResponse finished = (await a.DeleteAsync()).ShouldSucceed();

        finished.WasAlreadyDeleted.ShouldBeTrue();
        world.Store.BlobNames.ShouldBe([ShareLayout.For(shareId).Manifest]);
        a.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task OnAnotherMachine_DeleteInADeletedSharesFolder_Should_JustDropItsState()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("a.txt", "a"));
        (await a.DeleteAsync()).ShouldSucceed();
        Revision marker = (await world.HeadAsync(shareId)).Revision;

        DeleteShareResponse dropped = (await b.DeleteAsync()).ShouldSucceed();

        dropped.WasAlreadyDeleted.ShouldBeTrue();
        dropped.MarkerRevision.ShouldBe(marker);
        b.State.State.ShouldBeNull();
        b.Folder.ReadText("a.txt").ShouldBe("a");
        (await world.HeadAsync(shareId)).Revision.ShouldBe(marker);
    }

    [Fact]
    public async Task ACommitElsewhereFirst_Should_BeRetried_AndTheMarkerGoOnTopOfIt()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await TwoMachines(("a.txt", "a"));
        bool fired = false;
        world.Store.BeforeReplaceManifest = async id =>
        {
            if (!fired)
            {
                fired = true;
                await world.CommitElsewhereAsync(id);
            }
        };

        DeleteShareResponse deleted = (await a.DeleteAsync()).ShouldSucceed();

        deleted.MarkerRevision.ShouldBe(Rev(3)); // 2 was the other machine's commit
        (await world.HeadAsync(shareId)).IsDeleted.ShouldBeTrue();
        world.Time.Delays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ASyncThatUploadsJustBeforeTheDelete_Should_StopAndTakeItsUploadsBackOut()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("a.txt", "a"));
        b.Folder.WriteFile("new-secret.txt", "uploaded a moment too late");
        // a's delete completes while b is reading the file to upload it: a's sweep has already
        // run, so only b can take that object back out.
        b.Folder.OnOperation = (operation, path) =>
        {
            if (operation == FolderOperation.OpenRead && path.Value == "new-secret.txt")
            {
                b.Folder.OnOperation = null;
                a.DeleteAsync().GetAwaiter().GetResult().ShouldSucceed();
            }

            return null;
        };

        (await b.SyncAsync()).ShouldFail().Code.ShouldBe("Shares.Deleted");

        world.Store.BlobNames.ShouldBe([ShareLayout.For(shareId).Manifest]);
        b.Folder.ReadText("new-secret.txt").ShouldBe("uploaded a moment too late");
    }

    [Fact]
    public async Task CopiesTheStoreCanStillRestore_Should_BeReported()
    {
        (SyncWorld world, Machine a, _, _) = await TwoMachines(("a.txt", "a"));
        world.Store.RecoverableCopies = 4;

        (await a.DeleteAsync()).ShouldSucceed().RecoverableCopies.ShouldBe(4);
    }

    [Fact]
    public async Task Delete_Should_Refuse_ANonShareFolderWithoutAnId_AndUnknownOrInvalidIds()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");

        (await a.DeleteAsync()).ShouldFail().ShouldBe(ShareErrors.NotAShare());
        var missing = ShareId.New();
        (await a.DeleteAsync(missing.ToString())).ShouldFail().ShouldBe(ShareErrors.NotFound(missing));
        (await a.DeleteAsync("not-a-uuid")).ShouldFail().Code.ShouldBe("Shares.NameNotFound"); // not an ID, so a name nobody holds
    }

    [Fact]
    public async Task Delete_Should_FailWithFolderLocked_WhenASyncIsRunningInThisFolder()
    {
        (_, Machine a, _, _) = await TwoMachines(("a.txt", "a"));
        await using IAsyncDisposable other = a.Lock.HoldElsewhere();

        (await a.DeleteAsync()).ShouldFail().ShouldBe(Domain.State.StateErrors.FolderLocked);
    }

    [Fact]
    public async Task AfterDeletion_ThisFolder_Should_BeAbleToStartANewShare()
    {
        (SyncWorld world, Machine a, _, ShareId old) = await TwoMachines(("a.txt", "a"));
        (await a.DeleteAsync()).ShouldSucceed();

        ShareId fresh = (await a.CreateAsync()).ShouldSucceed().ShareId;

        fresh.ShouldNotBe(old);
        (await world.HeadAsync(fresh)).Entries.Count.ShouldBe(1);
    }
}
