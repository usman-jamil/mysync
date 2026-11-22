using MySync.Application.Abstractions.Storage;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Content;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>
/// <see cref="AzureStaticWebsitePublishTarget"/> against Azurite's publishing accounts. Azurite
/// gaps (CLAUDE.md, Publishing): no static website endpoint, so public verification is faked
/// elsewhere; no Put Blob From URL (501), so copies here take the blocks-from-URL path.
/// </summary>
public sealed class AzureStaticWebsitePublishTargetTests(AzuriteFixture azurite)
{
    private const string BaseUrl = "https://mysyncpublish.z13.web.core.windows.net";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AzureTransferSettings SmallCopyBlocks { get; } = AzureStores.TestSettings with { CopyBlockSize = 1024 * 1024 };

    private string PublishConnection()
    {
        Assert.SkipWhen(azurite.PublishConnectionString is null, $"No publishing account: set {AzuriteEnvironment.PublishConnectionStringVariable} with supplied storage.");
        return azurite.PublishConnectionString!;
    }

    private static AzureStaticWebsitePublishTarget Target(string? connectionString, string? baseUrl = BaseUrl, RecordingLogger? logger = null) =>
        new(Options.Create(new PublishStorageOptions { ConnectionString = connectionString, BaseUrl = baseUrl }), SmallCopyBlocks, logger);

    private static string Prefix() => $"p/{PublishId.New()}/";

    private BlobContainerClient Web() => new BlobServiceClient(PublishConnection()).GetBlobContainerClient("$web");

    [Fact]
    public async Task CheckStaticWebsite_Should_Pass_OnlyWithIndexHtml()
    {
        (await Target(PublishConnection()).CheckStaticWebsiteAsync(Ct)).IsSuccess.ShouldBeTrue();

        string? noWebsite = azurite.AccountConnectionString(AzuriteEnvironment.NoWebsiteAccount);
        string? wrongIndex = azurite.AccountConnectionString(AzuriteEnvironment.WrongIndexAccount);
        Assert.SkipWhen(noWebsite is null || wrongIndex is null, "Supplied storage has no accounts with other settings.");

        Error disabled = (await Target(noWebsite).CheckStaticWebsiteAsync(Ct)).Error;
        disabled.Code.ShouldBe("Publish.StaticWebsiteDisabled");
        disabled.Description.ShouldContain($"--account-name {AzuriteEnvironment.NoWebsiteAccount} --static-website --index-document index.html");
        (await Target(wrongIndex).CheckStaticWebsiteAsync(Ct)).Error.Code.ShouldBe("Publish.IndexDocumentMismatch");
    }

    [Fact]
    public async Task GetLocation_Should_NameTheAccountAndBaseUrl()
    {
        PublishLocation location = Target(PublishConnection()).GetLocation().Value;

        location.AccountName.ShouldBe(AzuriteEnvironment.AccountNameOf(PublishConnection()));
        location.BaseUrl.AbsoluteUri.ShouldBe(BaseUrl + "/");
    }

    [Theory]
    [InlineData(null, BaseUrl, "Configuration.Missing", "MYSYNC_PUBLISH_CONNECTION_STRING")]
    [InlineData("UseDevelopmentStorage=true", null, "Configuration.Missing", "MYSYNC_PUBLISH_BASE_URL")]
    [InlineData("UseDevelopmentStorage=true", "http://insecure.example", "Configuration.Invalid", "MYSYNC_PUBLISH_BASE_URL")]
    [InlineData("UseDevelopmentStorage=true", "not a url", "Configuration.Invalid", "MYSYNC_PUBLISH_BASE_URL")]
    [InlineData("UseDevelopmentStorage=true", "https://x.example/?sig=abc", "Configuration.Invalid", "MYSYNC_PUBLISH_BASE_URL")]
    [InlineData("AccountName=x;AccountKey=c2VjcmV0LWtleQ==;Garbage", BaseUrl, "Configuration.Invalid", "MYSYNC_PUBLISH_CONNECTION_STRING")]
    public void ASettingThatIsMissingOrMalformed_Should_BeAConfigurationError_WithoutItsValue(string? connection, string? baseUrl, string code, string variable)
    {
        Error error = Target(connection, baseUrl).GetLocation().Error;

        error.Code.ShouldBe(code);
        error.Description.ShouldContain(variable);
        error.Description.ShouldNotContain("c2VjcmV0LWtleQ==");
    }

    [Fact]
    public async Task Put_Should_StoreTheHeaders_AndOnlyIfAbsent_ShouldLeaveAnExistingBlob()
    {
        AzureStaticWebsitePublishTarget target = Target(PublishConnection());
        string name = Prefix() + "assets/app.js";
        PublishedBlob headers = new("text/javascript; charset=utf-8", "attachment; filename*=UTF-8''app.js", PublishedBlob.Immutable);

        (await target.PutAsync(name, new MemoryStream("first"u8.ToArray()), headers, onlyIfAbsent: true, Ct)).Value.ShouldBeTrue();
        (await target.PutAsync(name, new MemoryStream("second"u8.ToArray()), headers, onlyIfAbsent: true, Ct)).Value.ShouldBeFalse();

        BlobClient blob = Web().GetBlobClient(name);
        (await blob.DownloadContentAsync(Ct)).Value.Content.ToString().ShouldBe("first");
        BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentType.ShouldBe("text/javascript; charset=utf-8");
        properties.ContentDisposition.ShouldBe("attachment; filename*=UTF-8''app.js");
        properties.CacheControl.ShouldBe(PublishedBlob.Immutable);

        (await target.PutAsync(name, new MemoryStream("third"u8.ToArray()), headers, onlyIfAbsent: false, Ct)).Value.ShouldBeTrue();
        (await blob.DownloadContentAsync(Ct)).Value.Content.ToString().ShouldBe("third");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(3 * 1024 * 1024 + 5)]
    public async Task Copy_Should_CopyAnObjectFromThePrivateStore_ServerSide_WithItsHeaders(int size)
    {
        byte[] content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(size);
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf(content);
        AzureBlobShareStore source = AzureStores.Create(azurite.ConnectionString);
        (await source.EnsureReadyForNewShareAsync(Ct)).IsSuccess.ShouldBeTrue();
        (await source.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();
        Uri url = (await source.GetObjectReadUrlAsync(shareId, hash, TimeSpan.FromMinutes(5), Ct)).Value!;
        string name = Prefix() + "files/docs/a b [1] #?.pdf";
        PublishedBlob headers = new(ContentTypeMap.For(name), DispositionPolicy.ContentDisposition(name), PublishedBlob.Immutable);

        Result<CopyMethod> copied = await Target(PublishConnection()).CopyAsync(url, size, name, headers, Ct);

        copied.Value.ShouldBe(CopyMethod.BlocksFromUrl); // Azurite has no Put Blob From URL
        BlobClient blob = Web().GetBlobClient(name);
        (await blob.DownloadContentAsync(Ct)).Value.Content.ToArray().ShouldBe(content);
        BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentType.ShouldBe("application/pdf");
        properties.ContentDisposition.ShouldBe("inline; filename*=UTF-8''a%20b%20%5B1%5D%20#%3F.pdf");
        properties.CacheControl.ShouldBe(PublishedBlob.Immutable);
    }

    [Fact]
    public async Task DeletePrefix_Should_RemoveOnlyThatPublication_AndDeleteShouldAcceptAMissingBlob()
    {
        AzureStaticWebsitePublishTarget target = Target(PublishConnection());
        string mine = Prefix();
        string theirs = Prefix();
        foreach (string name in (string[])[mine + "index.html", mine + "files/a.txt", mine + "files/b/c.txt", theirs + "index.html"])
        {
            (await target.PutAsync(name, new MemoryStream("x"u8.ToArray()), new PublishedBlob("text/plain"), false, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await target.DeleteAsync(mine + "index.html", Ct)).IsSuccess.ShouldBeTrue();
        (await target.DeleteAsync(mine + "index.html", Ct)).IsSuccess.ShouldBeTrue();
        (await target.DeletePrefixAsync(mine, Ct)).Value.ShouldBe(2);

        List<string> left = [];
        await foreach (BlobItem item in Web().GetBlobsAsync(BlobTraits.None, BlobStates.None, "p/", Ct))
        {
            left.Add(item.Name);
        }

        left.ShouldNotContain(name => name.StartsWith(mine, StringComparison.Ordinal));
        left.ShouldContain(theirs + "index.html");
    }

    [Fact]
    public async Task AWrongKey_Should_BeThePublishingAccountsUnauthorized_WithNoKeyInAnyErrorOrLog()
    {
        string wrongKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("definitely-not-the-key-0123456789"));
        string connection = PublishConnection().Replace(AzuriteEnvironment.AzuriteKey, wrongKey, StringComparison.Ordinal);
        RecordingLogger logger = new();
        AzureStaticWebsitePublishTarget target = Target(connection, logger: logger);

        Error error = (await target.PutAsync(Prefix() + "x.txt", new MemoryStream("x"u8.ToArray()), new PublishedBlob("text/plain"), false, Ct)).Error;

        error.Code.ShouldBe("Publish.Unauthorized");
        error.Description.ShouldContain("MYSYNC_PUBLISH_CONNECTION_STRING");
        error.Description.ShouldNotContain(wrongKey);
        logger.Lines.ShouldAllBe(line => !line.Contains(wrongKey, StringComparison.Ordinal));
    }
}
