using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MySync.Application.Publishing.Publish;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// Publishing with the full DI graph (embedded site assets, Azure store and publish target) on
/// Azurite: a private account for the share and a publishing account with static website hosting.
/// </summary>
public sealed class PublishingTests(AzuriteFixture azurite) : IDisposable
{
    private readonly AzureMachine _a = new(azurite.ConnectionString, AzuriteEnvironment.NewContainerName());
    private readonly AzureMachine _b = new(azurite.ConnectionString, string.Empty);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BlobContainerClient Web() => new BlobServiceClient(azurite.PublishConnectionString).GetBlobContainerClient("$web");

    private async Task<ShareId> SharedAsync()
    {
        Assert.SkipWhen(azurite.PublishConnectionString is null, $"No publishing account: set {AzuriteEnvironment.PublishConnectionStringVariable} with supplied storage.");
        _a.PublishConnectionString = azurite.PublishConnectionString;
        _b.PublishConnectionString = azurite.PublishConnectionString;
        _b.Container = _a.Container;
        _a.Folder.WriteFile("docs/readme.md", "# hello").WriteFile("photos/über café/IMG 1.jpg", "jpeg bytes").WriteFile("site/index.html", "<script>alert(1)</script>").WriteFile("empty.txt", string.Empty);
        return await _a.CreateAsync();
    }

    [Fact]
    public async Task Publish_Should_PutTheWholeSiteOnTheStaticWebsite_AndVerifyIt()
    {
        ShareId shareId = await SharedAsync();

        PublishReport report = AzureMachine.Ok(await _a.PublishAsync(new PublishShareCommand(Title: "Q3 reports")));

        report.Verified.ShouldBeTrue(report.VerificationDetail);
        report.Files.ShouldBe(4);
        report.Url.AbsoluteUri.ShouldBe($"{AzureMachine.PublishBaseUrl}/p/{report.PublishId}/");
        var layout = PublicationLayout.For(report.PublishId);

        List<string> blobs = [];
        await foreach (BlobItem item in Web().GetBlobsAsync(BlobTraits.None, BlobStates.None, layout.Prefix, Ct))
        {
            blobs.Add(item.Name[layout.Prefix.Length..]);
        }

        blobs.Order(StringComparer.Ordinal).ShouldBe(
        [
            "SHA256SUMS.txt", "assets/THIRD-PARTY-NOTICES.txt", "assets/app.css", "assets/app.js", "download.ps1", "download.sh",
            "files/docs/readme.md", "files/empty.txt", "files/photos/über café/IMG 1.jpg", "files/site/index.html", "index.html", "site-data.js",
        ]);

        (await Web().GetBlobClient(layout.File(ManifestBuilder.PathOf("photos/über café/IMG 1.jpg"))).DownloadContentAsync(Ct)).Value.Content.ToString().ShouldBe("jpeg bytes");
        BlobProperties app = (await Web().GetBlobClient(layout.Blob("assets/app.js")).GetPropertiesAsync(cancellationToken: Ct)).Value;
        app.ContentType.ShouldBe("text/javascript; charset=utf-8");
        BlobProperties html = (await Web().GetBlobClient(layout.File(ManifestBuilder.PathOf("site/index.html"))).GetPropertiesAsync(cancellationToken: Ct)).Value;
        html.ContentDisposition.ShouldStartWith("attachment;");
        (await Web().GetBlobClient(layout.Index).DownloadContentAsync(Ct)).Value.Content.ToString().ShouldContain("./site-data.js");
        (await Web().GetBlobClient("robots.txt").ExistsAsync(Ct)).Value.ShouldBeTrue();

        string siteData = (await Web().GetBlobClient(layout.Blob("site-data.js")).DownloadContentAsync(Ct)).Value.Content.ToString();
        JsonNode data = JsonNode.Parse(siteData["window.__MYSYNC_SITE__ = ".Length..^2])!;
        data["publication"]!["title"]!.GetValue<string>().ShouldBe("Q3 reports");
        data["entries"]!.AsArray().Count.ShouldBe(4);
        siteData.ShouldNotContain(shareId.ToString());

        Publication record = AzureMachine.Ok(await _a.PublicationsAsync()).Publications.Single();
        record.Status.ShouldBe(PublicationStatus.Published);
        record.SiteAssetsVersion.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task Unpublish_FromAnotherMachine_And_Delete_Should_TakeEveryPublicationDown()
    {
        ShareId shareId = await SharedAsync();
        PublishReport first = AzureMachine.Ok(await _a.PublishAsync());
        PublishReport second = AzureMachine.Ok(await _a.PublishAsync());

        AzureMachine.Ok(await _b.UnpublishAsync(first.PublishId.Value, shareId.ToString()));
        (await Web().GetBlobClient(PublicationLayout.For(first.PublishId).Index).ExistsAsync(Ct)).Value.ShouldBeFalse();
        (await Web().GetBlobClient(PublicationLayout.For(second.PublishId).Index).ExistsAsync(Ct)).Value.ShouldBeTrue();

        Application.Shares.Delete.DeleteShareResponse deleted = AzureMachine.Ok(await _a.DeleteAsync());

        deleted.PublicationsRemoved.ShouldBe(1);
        List<string> left = [];
        await foreach (BlobItem item in Web().GetBlobsAsync(BlobTraits.None, BlobStates.None, PublicationLayout.For(second.PublishId).Prefix, Ct))
        {
            left.Add(item.Name);
        }

        left.ShouldBeEmpty();

        (await _b.WithStoreAsync(store => store.GetPublicationsAsync(shareId, Ct))).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Should_RefuseWithoutThePublishingSettings_WhenTheShareHasPublications()
    {
        await SharedAsync();
        AzureMachine.Ok(await _a.PublishAsync());
        _a.PublishConnectionString = null;

        (await _a.DeleteAsync()).Error.Code.ShouldBe("Publish.PublicationsNeedSettings");
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}
