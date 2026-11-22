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

    /// <summary>The public URL of the supplied publishing account's static website, for <see cref="ARealStaticWebsite_Should_ServeGzipFiles_SoEveryClientGetsTheOriginalBytes"/>.</summary>
    public const string PublishBaseUrlVariable = "MYSYNC_TEST_PUBLISH_BASE_URL";

    private static readonly byte[] LogText = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"2026-11-22 line {i} of a log\n")));

    [Fact]
    public async Task AGzipFile_Should_BePublishedAsStored_WithContentEncoding_ByPutAndByServerSideCopy()
    {
        string connection = PublishConnection();
        AzureStaticWebsitePublishTarget target = Target(connection);
        string prefix = Prefix();
        byte[] gzip = GzipBytes(LogText);
        PublishedBlob headers = new("text/plain; charset=utf-8", "inline", PublishedBlob.Immutable, "gzip");

        // Streamed through this machine:
        using (MemoryStream content = new(gzip))
        {
            (await target.PutAsync(prefix + "files/put.log", content, headers, onlyIfAbsent: false, Ct)).IsSuccess.ShouldBeTrue();
        }

        // Copied server-side from a share's gzip object:
        AzureBlobShareStore source = AzureStores.Create(azurite.ConnectionString);
        (await source.EnsureReadyForNewShareAsync(Ct)).IsSuccess.ShouldBeTrue();
        var shareId = ShareId.New();
        ContentHash hash = MySync.Testing.ManifestBuilder.HashOf(LogText);
        using (MemoryStream stored = new(gzip))
        {
            (await source.UploadObjectAsync(shareId, hash, new ObjectUpload(stored, ObjectEncoding.Gzip, LogText.LongLength, () => Result.Success()), Ct)).IsSuccess.ShouldBeTrue();
        }

        Uri url = (await source.GetObjectReadUrlAsync(shareId, hash, TimeSpan.FromMinutes(5), Ct)).Value!;
        (await target.CopyAsync(url, gzip.LongLength, prefix + "files/copy.log", headers, Ct)).IsSuccess.ShouldBeTrue();

        foreach (string name in new[] { "files/put.log", "files/copy.log" })
        {
            BlobClient blob = Web().GetBlobClient(prefix + name);
            BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value;
            properties.ContentEncoding.ShouldBe("gzip", name);
            (await blob.DownloadContentAsync(Ct)).Value.Content.ToArray().ShouldBe(gzip, name);
        }

        (await target.DeletePrefixAsync(prefix, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ARealStaticWebsite_Should_ServeGzipFiles_SoEveryClientGetsTheOriginalBytes()
    {
        // Only real Azure has a static website endpoint (Azurite has none): real-azure.yml runs this.
        string? baseUrl = Environment.GetEnvironmentVariable(PublishBaseUrlVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(baseUrl) || azurite.PublishConnectionString is null, $"Needs a real publishing account: {AzuriteEnvironment.PublishConnectionStringVariable} and {PublishBaseUrlVariable}.");
        AzureStaticWebsitePublishTarget target = Target(azurite.PublishConnectionString, baseUrl);
        string prefix = Prefix();
        byte[] gzip = GzipBytes(LogText);
        using (MemoryStream content = new(gzip))
        {
            (await target.PutAsync(prefix + "files/app.log", content, new PublishedBlob("text/plain; charset=utf-8", "inline", PublishedBlob.Immutable, "gzip"), onlyIfAbsent: false, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        Uri file = new(new Uri(baseUrl!.TrimEnd('/') + "/"), prefix + "files/app.log");
        try
        {
            // 1. Served as stored, and saying so (no decoding in this client).
            using HttpClientHandler rawHandler = new() { AutomaticDecompression = System.Net.DecompressionMethods.None, CheckCertificateRevocationList = true };
            using (HttpClient raw = new(rawHandler, disposeHandler: false))
            {
                using HttpResponseMessage response = await raw.GetAsync(file, Ct);
                response.EnsureSuccessStatusCode();
                response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
                (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(gzip);
            }

            // 2. What a browser does: ask for gzip, decode it.
            using HttpClientHandler browserHandler = new() { AutomaticDecompression = System.Net.DecompressionMethods.GZip, CheckCertificateRevocationList = true };
            using (HttpClient browser = new(browserHandler, disposeHandler: false))
            {
                (await browser.GetByteArrayAsync(file, Ct)).ShouldBe(LogText);
            }

            // 3. curl --compressed (download.sh) and 4. Invoke-WebRequest -OutFile (download.ps1).
            if (await RunsAsync("curl", "--version"))
            {
                (await FetchWithAsync("curl", ["-fsSL", "--compressed", "-o", "{out}", file.AbsoluteUri])).ShouldBe(LogText);
            }

            if (await RunsAsync("pwsh", "-NoProfile", "-Command", "exit 0"))
            {
                byte[] fetched = await FetchWithAsync("pwsh", ["-NoProfile", "-Command", $"Invoke-WebRequest -UseBasicParsing -Uri '{file.AbsoluteUri}' -OutFile '{{out}}'"]);
                // PowerShell 7 decodes; if one doesn't, download.ps1 decodes it itself (GzipDownloadScriptTests).
                (fetched.SequenceEqual(LogText) || fetched.SequenceEqual(gzip)).ShouldBeTrue();
            }
        }
        finally
        {
            await target.DeletePrefixAsync(prefix, CancellationToken.None);
        }
    }

    private static byte[] GzipBytes(byte[] content)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.GZipStream gzip = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static async Task<bool> RunsAsync(string program, params string[] arguments)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            await process.WaitForExitAsync(Ct);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<byte[]> FetchWithAsync(string program, string[] arguments)
    {
        string output = Path.Combine(Path.GetTempPath(), $"mysync-{Guid.NewGuid():N}");
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, [.. arguments.Select(argument => argument.Replace("{out}", output, StringComparison.Ordinal))])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            string error = await process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            process.ExitCode.ShouldBe(0, error);
            return await File.ReadAllBytesAsync(output, Ct);
        }
        finally
        {
            File.Delete(output);
        }
    }

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
