using MySync.Application.Shares.Create;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Shares;

/// <summary><c>mysync sync</c> in a folder that isn't a share yet: preview, then create (CLAUDE.md §9).</summary>
public class CreateShareTests
{
    [Fact]
    public async Task Preview_Should_CountFilesAndBytes_WithoutHashing_AndSkipIgnoredFiles()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("a.txt", "12345").WriteFile("docs/b.md", "123").WriteFile("secret.txt", "xxxxxxxxxx");
        a.IgnoreRules.Matcher = new PathListIgnoreMatcher("secret.txt");

        NewSharePreview preview = (await a.PreviewAsync()).ShouldSucceed();

        preview.ExistingShareId.ShouldBeNull();
        preview.FileCount.ShouldBe(2);
        preview.TotalBytes.ShouldBe(8);
        preview.IsUnsafeRoot.ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_Should_SayTheFolderIsAlreadyAShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        (await a.PreviewAsync()).ShouldSucceed().ExistingShareId.ShouldBe(created.ShareId);
    }

    [Fact]
    public async Task Preview_Should_FailOnADamagedStateFile_NeverReportANewShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.State.LoadError = StateErrors.Corrupt("not JSON");

        (await a.PreviewAsync()).ShouldFail().Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public async Task Create_Should_UploadEveryFile_CommitRevisionOne_AndWriteTheState()
    {
        SyncWorld world = new();
        Machine a = world.Machine("usman@DEV-LAPTOP");
        a.Folder.WriteFile("docs/readme.md", "# hi").WriteFile("run.sh", "#!/bin/sh", executable: true).AddDirectory("logs");

        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        world.Store.IsReady.ShouldBeTrue();
        created.FileCount.ShouldBe(2);
        created.TotalBytes.ShouldBe(13);
        created.Uploaded.ShouldBe(new TransferTotals(2, 13));
        created.Skipped.ShouldBeEmpty();

        Manifest head = await world.HeadAsync(created.ShareId);
        head.Revision.ShouldBe(Revision.Initial);
        head.UpdatedBy.ShouldBe("usman@DEV-LAPTOP");
        head.Entries.Keys.Select(path => path.Value).ShouldBe(["docs/readme.md", "logs", "run.sh"]);
        head.Entries[ManifestBuilder.PathOf("run.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();

        var layout = ShareLayout.For(created.ShareId);
        world.Store.BlobNames.ShouldBe(
            [layout.Manifest, layout.ManifestHistory(Revision.Initial), layout.ObjectBlob(ManifestBuilder.HashOf("# hi")), layout.ObjectBlob(ManifestBuilder.HashOf("#!/bin/sh"))],
            ignoreOrder: true);

        ShareState state = a.State.State!;
        state.ShareId.ShouldBe(created.ShareId);
        state.BaseRevision.ShouldBe(Revision.Initial);
        state.Base.ShouldBe(head.Entries);
        state.HashCache.Count.ShouldBe(2);
        state.Location.ShouldBe(InMemoryShareStore.DefaultLocation);
        a.Lock.IsHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Create_Should_StoreIdenticalFilesOnce()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("one.txt", "same").WriteFile("two.txt", "same");

        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        created.FileCount.ShouldBe(2);
        created.Uploaded.Files.ShouldBe(1);
        world.Store.BlobNames.Count(name => name.Contains("/objects/", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_Should_Refuse_WhenTheFolderIsAlreadyAShare()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        CreateShareResponse first = (await a.CreateAsync()).ShouldSucceed();

        Error error = (await a.CreateAsync()).ShouldFail();

        error.ShouldBe(Domain.Shares.ShareErrors.AlreadyAShare(first.ShareId));
    }

    [Fact]
    public async Task Create_Should_RefuseAnUnsafeRoot_UnlessForced()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.IsUnsafeRoot = true;

        (await a.CreateAsync()).ShouldFail().Code.ShouldBe("Shares.UnsafeRoot");
        world.Store.IsReady.ShouldBeFalse();
        a.State.State.ShouldBeNull();

        (await a.CreateAsync(new CreateShareCommand(Force: true))).ShouldSucceed();
    }

    [Fact]
    public async Task Create_Should_LeaveOutAFileThatChangesWhileUploading_AndAddItOnTheNextSync()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("stable.txt", "stable").WriteFile("busy.log", "v1");
        a.Folder.OnOperation = (operation, path) =>
        {
            if (operation == FolderOperation.OpenRead && path.Value == "busy.log")
            {
                a.Folder.OnOperation = null;
                a.Folder.WriteFile("busy.log", "v2, written mid-upload");
            }

            return null;
        };

        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        created.Skipped.Single().ShouldBe(new SkippedPath("busy.log", SkipCause.Unstable, "it changed while it was being uploaded"));
        (await world.HeadAsync(created.ShareId)).Entries.Keys.Select(path => path.Value).ShouldBe(["stable.txt"]);
        a.State.State!.Base.ContainsKey(ManifestBuilder.PathOf("busy.log")).ShouldBeFalse();

        SyncReport next = (await a.SyncAsync()).ShouldSucceed();

        next.Committed.ShouldBeTrue();
        (await world.HeadAsync(created.ShareId)).Entries.Keys.Select(path => path.Value).ShouldBe(["busy.log", "stable.txt"]);
    }

    [Fact]
    public async Task Create_Should_WarnAboutCaseOnlyCollisions_ButStillUploadBoth()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("Foo.txt", "upper").WriteFile("foo.txt", "lower");

        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        created.FileCount.ShouldBe(2);
        created.Warnings.Single().ShouldContain("differ only in case");
    }

    [Fact]
    public async Task Create_Should_Succeed_WhenTheHistoryCopyFails_AndSaySo()
    {
        SyncWorld world = new();
        world.Store.FailHistoryWrites = true;
        Machine a = world.Machine("a");

        CreateShareResponse created = (await a.CreateAsync()).ShouldSucceed();

        created.Warnings.Single().ShouldContain("history copy");
        (await world.HeadAsync(created.ShareId)).Revision.ShouldBe(Revision.Initial);
    }

    [Fact]
    public async Task Create_Should_FailWithFolderLocked_WhenAnotherProcessHoldsTheLock()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        await using IAsyncDisposable other = a.Lock.HoldElsewhere();

        (await a.CreateAsync()).ShouldFail().ShouldBe(StateErrors.FolderLocked);
    }

    [Fact]
    public async Task Create_Should_RejectAnInvalidParallelism()
    {
        SyncWorld world = new();

        Error error = (await world.Machine("a").CreateAsync(new CreateShareCommand(Parallelism: 0))).ShouldFail();

        error.ShouldBeOfType<ValidationError>().Errors.Single().Code.ShouldBe("Sync.InvalidParallelism");
    }
}
