using System.Net;
using System.Text.Json.Nodes;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Publishing.Publish;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>Publishing an Azure share to the Azure test account's static website.</summary>
public sealed class AzurePublishingTests(StorageFixture storage) : PublishingTests(storage.Azure, storage.Azure);

/// <summary>Publishing an S3 share to the public S3 test bucket (native copies inside S3).</summary>
public sealed class S3PublishingTests(StorageFixture storage) : PublishingTests(storage.S3, storage.S3);

/// <summary>An Azure share published to S3: neither can read the other's objects, so every file is streamed.</summary>
public sealed class AzureToS3PublishingTests(StorageFixture storage) : PublishingTests(storage.Azure, storage.S3);

/// <summary>An S3 share published to Azure: Put Blob From URL reads the share's presigned URLs.</summary>
public sealed class S3ToAzurePublishingTests(StorageFixture storage) : PublishingTests(storage.S3, storage.Azure);

/// <summary>
/// Publishing with the full DI graph (embedded site assets, the share's store, the publish target),
/// on real storage, checked the way the public sees it: anonymous HTTP through the base URL.
/// </summary>
public abstract class PublishingTests : IDisposable
{
    private readonly Backend _shares;
    private readonly Backend _publishing;
    private readonly Machine _a;
    private readonly Machine _b;
    private readonly HttpClient _anonymous = new();

    protected PublishingTests(Backend shares, Backend publishing)
    {
        ArgumentNullException.ThrowIfNull(shares);
        _shares = shares;
        _publishing = publishing;
        string place = shares.NewPlace();
        _a = new Machine(shares, place);
        _b = new Machine(shares, place);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ShareId> SharedAsync()
    {
        _ = _publishing.PublishBaseUrl; // skips (or fails in CI) without a publishing side
        _a.Publishing = _publishing;
        _b.Publishing = _publishing;
        _a.Folder.WriteFile("docs/readme.md", "# hello").WriteFile("photos/über café/IMG 1.jpg", "jpeg bytes").WriteFile("site/index.html", "<script>alert(1)</script>").WriteFile("empty.txt", string.Empty);
        return await _a.CreateAsync();
    }

    [Fact]
    public async Task Publish_Should_PutTheWholeSiteUp_AndVerifyIt_Anonymously()
    {
        ShareId shareId = await SharedAsync();

        PublishReport report = Machine.Ok(await _a.PublishAsync(new PublishShareCommand(Title: "Q3 reports")));

        report.Verified.ShouldBeTrue(report.VerificationDetail);
        report.Files.ShouldBe(4);
        string folder = $"{_publishing.PublishBaseUrl}/p/{report.PublishId}/";
        report.FolderUrl.AbsoluteUri.ShouldBe(folder);
        report.Url.AbsoluteUri.ShouldBe(_publishing.Provider == "s3" ? folder + "index.html" : folder);
        report.Copies.Keys.ShouldBe([ExpectedCopy()]);
        var layout = PublicationLayout.For(report.PublishId);

        List<string> blobs = [.. (await _publishing.ListPublishedAsync(layout.Prefix)).Select(name => name[layout.Prefix.Length..])];
        blobs.Order(StringComparer.Ordinal).ShouldBe(
        [
            "SHA256SUMS.txt", "assets/THIRD-PARTY-NOTICES.txt", "assets/app.css", "assets/app.js", "download.ps1", "download.sh",
            "files/docs/readme.md", "files/empty.txt", "files/photos/über café/IMG 1.jpg", "files/site/index.html", "index.html", "site-data.js",
        ]);

        (await GetStringAsync(folder + "files/photos/" + Uri.EscapeDataString("über café") + "/IMG%201.jpg")).ShouldBe("jpeg bytes");
        using (HttpResponseMessage app = await GetAsync(folder + "assets/app.js"))
        {
            app.Content.Headers.ContentType!.ToString().ShouldBe("text/javascript; charset=utf-8");
        }

        using (HttpResponseMessage html = await GetAsync(folder + "files/site/index.html"))
        {
            html.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        }

        (await GetStringAsync(report.Url.AbsoluteUri)).ShouldContain("./site-data.js");
        (await GetAsync($"{_publishing.PublishBaseUrl}/robots.txt")).Dispose();

        string siteData = await GetStringAsync(folder + "site-data.js");
        JsonNode data = JsonNode.Parse(siteData["window.__MYSYNC_SITE__ = ".Length..^2])!;
        data["publication"]!["title"]!.GetValue<string>().ShouldBe("Q3 reports");
        data["entries"]!.AsArray().Count.ShouldBe(4);
        siteData.ShouldNotContain(shareId.ToString());

        Publication record = Machine.Ok(await _a.PublicationsAsync()).Publications.Single();
        record.Status.ShouldBe(PublicationStatus.Published);
        record.SiteAssetsVersion.ShouldStartWith("sha256:");
        record.Url.ShouldBe(report.Url.AbsoluteUri);
    }

    [Fact]
    public async Task Unpublish_FromAnotherMachine_And_Delete_Should_TakeEveryPublicationDown()
    {
        ShareId shareId = await SharedAsync();
        PublishReport first = Machine.Ok(await _a.PublishAsync());
        PublishReport second = Machine.Ok(await _a.PublishAsync());

        Machine.Ok(await _b.UnpublishAsync(first.PublishId.Value, shareId.ToString()));
        (await _publishing.ListPublishedAsync(PublicationLayout.For(first.PublishId).Prefix)).ShouldBeEmpty();
        (await _publishing.ListPublishedAsync(PublicationLayout.For(second.PublishId).Index)).ShouldNotBeEmpty();
        using (HttpResponseMessage gone = await _anonymous.GetAsync(first.Url, Ct))
        {
            ((int)gone.StatusCode).ShouldBeOneOf([.. NotServed()]);
        }

        Application.Shares.Delete.DeleteShareResponse deleted = Machine.Ok(await _a.DeleteAsync());

        deleted.PublicationsRemoved.ShouldBe(1);
        (await _publishing.ListPublishedAsync(PublicationLayout.For(second.PublishId).Prefix)).ShouldBeEmpty();
        (await _b.WithStoreAsync(store => store.GetPublicationsAsync(shareId, Ct))).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Should_RefuseWithoutThePublishingSettings_WhenTheShareHasPublications()
    {
        await SharedAsync();
        Machine.Ok(await _a.PublishAsync());
        _a.Publishing = null;

        (await _a.DeleteAsync()).Error.Code.ShouldBe("Publish.PublicationsNeedSettings");

        _a.Publishing = _publishing;
        Machine.Ok(await _a.DeleteAsync()); // and with them, everything goes
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _anonymous.Dispose();
        GC.SuppressFinalize(this);
    }

    // Within one provider the copy stays server-side; across providers only Azure can read the other's (presigned) URLs.
    private CopyMethod ExpectedCopy() => (_shares.Provider, _publishing.Provider) switch
    {
        ("azure-blob", "azure-blob") or ("s3", "azure-blob") => CopyMethod.PutBlobFromUrl,
        ("s3", "s3") => CopyMethod.CopyObject,
        _ => CopyMethod.Streamed,
    };

    private int[] NotServed() => _publishing.Provider == "s3" ? [403, 404] : [404];

    private async Task<HttpResponseMessage> GetAsync(string url)
    {
        HttpResponseMessage response = await _anonymous.GetAsync(new Uri(url), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
        return response;
    }

    private async Task<string> GetStringAsync(string url)
    {
        using HttpResponseMessage response = await GetAsync(url);
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
