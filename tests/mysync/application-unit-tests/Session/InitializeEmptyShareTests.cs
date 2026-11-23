using MySync.Application.Session;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Session;

/// <summary><c>mysync tui --name</c> in a folder that isn't a share: an empty share, nothing uploaded (CLAUDE.md §10d).</summary>
public class InitializeEmptyShareTests
{
    [Fact]
    public async Task AnEmptyFolder_Should_GetAnEmptyShare_AndAnEmptyBase()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");

        InitializeEmptyShareResponse created = (await laptop.InitializeAsync("app-config")).ShouldSucceed();

        Manifest head = await world.HeadAsync(created.ShareId);
        head.Revision.Value.ShouldBe(1);
        head.Entries.ShouldBeEmpty();
        head.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);
        laptop.State.State!.ShareId.ShouldBe(created.ShareId);
        laptop.State.State.Base.ShouldBeEmpty();
        (await world.Aliases.GetAsync(ShareName.Create("app-config").Value, SyncWorld.Ct)).Value!.Alias.ShareId.ShouldBe(created.ShareId);
    }

    [Fact]
    public async Task AFullFolder_Should_UploadNothing_AndEveryFileShowsAsNewHere()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder.WriteFile("a.txt", "a").WriteFile("docs/b.md", "b");

        (await laptop.InitializeAsync("app-config")).ShouldSucceed();
        FolderStatus status = (await laptop.FolderStatusAsync()).ShouldSucceed();

        world.Store.ObjectUploads.ShouldBe(0);
        status.IsShareEmpty.ShouldBeTrue();
        status.Entries.Select(entry => (entry.Path, entry.Status)).ShouldBe([("a.txt", FileStatus.NewHere), ("docs/b.md", FileStatus.NewHere)]);
    }

    [Fact]
    public async Task Compress_Should_StartTheShareCompressed()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");

        InitializeEmptyShareResponse created = (await laptop.InitializeAsync("app-config", compress: true)).ShouldSucceed();

        created.Compression.ShouldBe(CompressionSetting.Gzip);
        Manifest head = await world.HeadAsync(created.ShareId);
        head.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        head.Compression.ShouldBe(CompressionSetting.Gzip);
    }

    [Fact]
    public async Task ATakenName_Should_BeRefused_AndNothingWritten()
    {
        SyncWorld world = new();
        Machine first = world.Machine("usman@DEV-LAPTOP");
        Machine second = world.Machine("ali@SERVER01");
        (await first.InitializeAsync("app-config")).ShouldSucceed();

        SharedKernel.Error error = (await second.InitializeAsync("app-config")).ShouldFail();

        error.Code.ShouldBe("Shares.NameTaken");
        second.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task LosingTheRaceForTheName_Should_BeRefused()
    {
        SyncWorld world = new();
        Machine winner = world.Machine("usman@DEV-LAPTOP");
        Machine loser = world.Machine("ali@SERVER01");
        world.Aliases.BeforeCreate = async _ =>
        {
            world.Aliases.BeforeCreate = null;
            (await winner.InitializeAsync("app-config")).ShouldSucceed();
        };

        (await loser.InitializeAsync("app-config")).ShouldFail().Code.ShouldBe("Shares.NameTaken");
        loser.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task AFolderThatIsAShare_AndAnUnsafeRoot_Should_BeRefused()
    {
        SyncWorld world = new();
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        (await laptop.InitializeAsync("app-config")).ShouldSucceed();
        Machine home = world.Machine("root@HOME");
        home.Folder.IsUnsafeRoot = true;

        (await laptop.InitializeAsync("other")).ShouldFail().Code.ShouldBe("Shares.AlreadyAShare");
        (await home.InitializeAsync("other")).ShouldFail().Code.ShouldBe("Shares.UnsafeRoot");
    }
}
