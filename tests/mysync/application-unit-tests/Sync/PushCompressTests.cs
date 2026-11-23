using MySync.Application.Shares.Create;
using MySync.Application.Sync.Push;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// <c>mysync push --create --compress</c> (CLAUDE.md §10b): a share the push creates starts with
/// compression on; for a share that exists, <c>--compress</c> changes nothing and says so.
/// </summary>
public class PushCompressTests
{
    private const string Target = "web01/20261116-090000Z";
    private const string Warning = "Compression is a setting of the share. Turn it on with: mysync compression on --share logs";

    // Compressible and over the policy's 512-byte floor, so a compressed share stores it gzip.
    private static readonly string Log = string.Concat(Enumerable.Repeat("GET /index.html 200 0.003s\n", 100));

    private static ShareReference Logs => ShareReference.Parse("logs").Value;

    private static Machine Server(SyncWorld world, string name = "svc@web01")
    {
        Machine server = world.Machine(name);
        server.Sources.WriteFile("/srv/app/access.log", Log).WriteFile("/srv/app/small.txt", "tiny");
        server.PushRecords.PadTo = 2000;
        return server;
    }

    private static async Task<ShareId> CreateShareAsync(SyncWorld world, bool compress)
    {
        Machine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Folder.WriteFile("docs/readme.md", "hello");
        return (await laptop.CreateAsync(new CreateShareCommand(ShareName.Create("logs").Value, Compress: compress))).ShouldSucceed().ShareId;
    }

    private static string? EncodingOf(SyncWorld world, ShareId shareId, Manifest head, string path) =>
        world.Store.ContentEncodingOf(shareId, ((FileEntry)head.Entries[SharePath.Create(path).Value]).Sha256);

    [Fact]
    public async Task CreateWithCompress_Should_StartTheShareCompressed_AndCompressThisPushsUploads_RecordIncluded()
    {
        SyncWorld world = new();
        Machine server = Server(world);

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true, Compress: true))).ShouldSucceed();

        report.Created.ShouldBeTrue();
        report.Compression.ShouldBe(CompressionSetting.Gzip);
        report.Warnings.ShouldBeEmpty();
        report.Uploaded.Compressed.ShouldBeTrue();

        Manifest head = await world.HeadAsync(report.ShareId!);
        head.Compression.ShouldBe(CompressionSetting.Gzip);
        head.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        world.Store.BlobNames.ShouldContain($"shares/{report.ShareId}/manifests/0000000001.json");
        Manifest first = (await world.Store.ListManifestHistoryAsync(report.ShareId!, SyncWorld.Ct).FirstAsync(SyncWorld.Ct)).Value;
        first.Revision.Value.ShouldBe(1);
        first.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion); // compressed from the very first manifest
        first.Compression.ShouldBe(CompressionSetting.Gzip);

        EncodingOf(world, report.ShareId!, head, $"{Target}/access.log").ShouldBe("gzip");
        EncodingOf(world, report.ShareId!, head, $"{Target}/{PushRecord.FileName}").ShouldBe("gzip");
        EncodingOf(world, report.ShareId!, head, $"{Target}/small.txt").ShouldBeNull(); // the policy keeps tiny files raw
    }

    [Fact]
    public async Task CreateWithoutCompress_Should_StayUncompressed()
    {
        SyncWorld world = new();
        Machine server = Server(world);

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true))).ShouldSucceed();

        report.Compression.ShouldBe(CompressionSetting.None);
        Manifest head = await world.HeadAsync(report.ShareId!);
        head.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);
        EncodingOf(world, report.ShareId!, head, $"{Target}/access.log").ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompressForAnExistingShareWithCompressionOff_Should_Warn_AndStoreRaw(bool create)
    {
        SyncWorld world = new();
        ShareId shareId = await CreateShareAsync(world, compress: false);
        Machine server = Server(world);

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: create, Compress: true))).ShouldSucceed();

        report.Created.ShouldBeFalse();
        report.Compression.ShouldBe(CompressionSetting.None);
        report.Warnings.ShouldBe([Warning]);
        Manifest head = await world.HeadAsync(shareId);
        head.Compression.ShouldBe(CompressionSetting.None);
        head.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);
        EncodingOf(world, shareId, head, $"{Target}/access.log").ShouldBeNull();
        EncodingOf(world, shareId, head, $"{Target}/{PushRecord.FileName}").ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APushIntoAShareWithCompressionOn_Should_StoreGzip_WithNoWarning(bool compress)
    {
        SyncWorld world = new();
        ShareId shareId = await CreateShareAsync(world, compress: true);
        Machine server = Server(world);

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: compress, Compress: compress))).ShouldSucceed();

        report.Compression.ShouldBe(CompressionSetting.Gzip);
        report.Warnings.ShouldBeEmpty();
        Manifest head = await world.HeadAsync(shareId);
        EncodingOf(world, shareId, head, $"{Target}/access.log").ShouldBe("gzip");
        EncodingOf(world, shareId, head, $"{Target}/{PushRecord.FileName}").ShouldBe("gzip");
    }

    [Fact]
    public async Task LosingTheCreateRace_Should_PushWithTheWinnersSetting_AndWarnWhenItIsOff()
    {
        SyncWorld world = new();
        Machine server = Server(world);
        Machine other = Server(world, "svc@web02");
        world.Aliases.BeforeCreate = async _ =>
        {
            world.Aliases.BeforeCreate = null;
            (await other.PushAsync(new PushCommand(Logs, [], Create: true))).ShouldSucceed();
        };

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true, Compress: true))).ShouldSucceed();

        report.Created.ShouldBeFalse();
        report.Compression.ShouldBe(CompressionSetting.None);
        report.Warnings.ShouldBe([Warning]);
        Manifest head = await world.HeadAsync(report.ShareId!);
        head.Revision.Value.ShouldBe(3);
        head.Compression.ShouldBe(CompressionSetting.None);
        EncodingOf(world, report.ShareId!, head, $"{Target}/access.log").ShouldBeNull();
    }

    [Fact]
    public async Task LosingTheCreateRaceToACompressedShare_Should_NotWarn()
    {
        SyncWorld world = new();
        Machine server = Server(world);
        Machine other = Server(world, "svc@web02");
        world.Aliases.BeforeCreate = async _ =>
        {
            world.Aliases.BeforeCreate = null;
            (await other.PushAsync(new PushCommand(Logs, [], Create: true, Compress: true))).ShouldSucceed();
        };

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true, Compress: true))).ShouldSucceed();

        report.Created.ShouldBeFalse();
        report.Compression.ShouldBe(CompressionSetting.Gzip);
        report.Warnings.ShouldBeEmpty();
        Manifest head = await world.HeadAsync(report.ShareId!);
        EncodingOf(world, report.ShareId!, head, $"{Target}/access.log").ShouldBe("gzip");
    }

    [Fact]
    public async Task DryRunOfCreateWithCompress_Should_ReportGzip_AndCreateNothing()
    {
        SyncWorld world = new();
        Machine server = Server(world);

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true, DryRun: true, Compress: true))).ShouldSucceed();

        report.DryRun.ShouldBeTrue();
        report.Created.ShouldBeTrue();
        report.Compression.ShouldBe(CompressionSetting.Gzip);
        report.Warnings.ShouldBeEmpty();
        (await world.Aliases.GetAsync(ShareName.Create("logs").Value, SyncWorld.Ct)).Value.ShouldBeNull();
        world.Store.ObjectUploads.ShouldBe(0);
    }

    [Fact]
    public async Task DryRunIntoAnExistingUncompressedShare_Should_Warn()
    {
        SyncWorld world = new();
        await CreateShareAsync(world, compress: false);
        Machine server = Server(world);
        int uploads = world.Store.ObjectUploads;

        PushReport report = (await server.PushAsync(new PushCommand(Logs, [], Create: true, DryRun: true, Compress: true))).ShouldSucceed();

        report.Created.ShouldBeFalse();
        report.Compression.ShouldBe(CompressionSetting.None);
        report.Warnings.ShouldBe([Warning]);
        world.Store.ObjectUploads.ShouldBe(uploads);
    }
}
