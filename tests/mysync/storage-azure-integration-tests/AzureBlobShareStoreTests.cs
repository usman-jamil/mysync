using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>What only the Azure store does, beyond the shared contract (CLAUDE.md §6, §11).</summary>
public class AzureBlobShareStoreTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFileLargerThanABlock_Should_UploadAsSeveralBlocks_AndReadBackByteForByte()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 123); // 5 MB, 1 MiB blocks
        var hash = ContentHash.FromBytes(SHA256.HashData(content));

        (await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        BlockList blocks = (await ObjectBlob(store, shareId, hash).GetBlockListAsync(BlockListTypes.Committed, cancellationToken: Ct)).Value;
        blocks.CommittedBlocks.Count().ShouldBe(6);
        await using StoredObject stored = (await store.OpenObjectReadAsync(shareId, hash, Ct)).Value;
        Stream read = stored.Content;
        using MemoryStream copy = new();
        await read.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(content);
    }

    [Fact]
    public async Task AnUploadWhoseBytesDontMatch_Should_LeaveNoVisibleBlob_EvenAfterStagingBlocks()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        ContentHash claimed = ManifestBuilder.HashOf("something else");

        Result result = await store.UploadObjectAsync(shareId, claimed, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct);

        result.Error.Code.ShouldBe("Storage.HashMismatch");
        (await ObjectBlob(store, shareId, claimed).ExistsAsync(Ct)).Value.ShouldBeFalse();
        (await store.ObjectExistsAsync(shareId, claimed, Ct)).Value.ShouldBeFalse();
        (await ToListAsync(store.ListObjectsAsync(shareId, Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUploadShorterThanItsDeclaredLength_Should_BeAMismatch()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        byte[] content = "abc"u8.ToArray();
        var hash = ContentHash.FromBytes(SHA256.HashData(content));

        (await store.UploadObjectAsync(ShareId.New(), hash, ObjectUpload.Raw(new MemoryStream(content), 4), Ct)).Error.Code.ShouldBe("Storage.HashMismatch");
    }

    [Fact]
    public async Task Objects_Should_CarryTheirHashAndSize_AsOctetStream()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf("hello");

        await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream("hello"u8.ToArray()), 5), Ct);

        BlobProperties properties = (await ObjectBlob(store, shareId, hash).GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentType.ShouldBe("application/octet-stream");
        properties.Metadata["sha256"].ShouldBe(hash.Value);
        properties.Metadata["size"].ShouldBe("5");
    }

    [Fact]
    public async Task AGzipObject_Should_CarryContentEncoding_TheUncompressedSize_AndItsStoredSize()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] original = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a log line that repeats\n", 2000)));
        ContentHash hash = ManifestBuilder.HashOf(original);
        using MemoryStream compressed = new();
        await using (System.IO.Compression.GZipStream gzip = new(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(original, Ct);
        }

        long storedLength = compressed.Length;
        compressed.Position = 0;
        (await store.UploadObjectAsync(shareId, hash, new ObjectUpload(compressed, Domain.Content.ObjectEncoding.Gzip, original.LongLength, () => Result.Success()), Ct)).IsSuccess.ShouldBeTrue();

        BlobProperties properties = (await ObjectBlob(store, shareId, hash).GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentEncoding.ShouldBe("gzip");
        properties.ContentType.ShouldBe("application/octet-stream");
        properties.ContentLength.ShouldBe(storedLength);
        properties.Metadata["size"].ShouldBe(original.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        properties.Metadata["storedsize"].ShouldBe(storedLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A plain GET answers with "Content-Encoding: gzip", and the SDK still hands back the
        // compressed bytes: nothing in the client or its transport decodes them.
        global::Azure.Response<BlobDownloadStreamingResult> download = await ObjectBlob(store, shareId, hash).DownloadStreamingAsync(cancellationToken: Ct);
        download.Value.Details.ContentEncoding.ShouldBe("gzip");
        using MemoryStream body = new();
        await download.Value.Content.CopyToAsync(body, Ct);
        body.ToArray().ShouldBe(compressed.ToArray());
    }

    [Fact]
    public async Task ARawObject_Should_HaveNoContentEncoding_NorStoredSize()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf("hello");

        await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream("hello"u8.ToArray()), 5), Ct);

        BlobProperties properties = (await ObjectBlob(store, shareId, hash).GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentEncoding.ShouldBeNull();
        properties.Metadata.ContainsKey("storedsize").ShouldBeFalse();
    }

    [Fact]
    public async Task TheSameObjectUploadedTwiceAtOnce_Should_SucceedBoth_AndBeStoredOnce()
    {
        AzureBlobShareStore first = await ReadyStoreAsync();
        AzureBlobShareStore second = AzureStores.Create(azurite.ConnectionString, first.GetLocation().Value.Container);
        var shareId = ShareId.New();
        byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var hash = ContentHash.FromBytes(SHA256.HashData(content));

        Result[] results = await Task.WhenAll(
            first.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct),
            second.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct));

        results.ShouldAllBe(result => result.IsSuccess);
        (await ToListAsync(first.ListObjectsAsync(shareId, Ct))).Single().Value.Size.ShouldBe(content.Length);
    }

    [Fact]
    public async Task TheManifestBlob_Should_BeExactlyTheSerializersBytes_AsJson()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().WithFile("docs/a.md", "a").WithDirectory("logs").Build();

        await store.CreateManifestAsync(manifest.ShareId, manifest, Ct);

        BlobClient blob = AzureStores.Container(azurite.ConnectionString, store).GetBlobClient(ShareLayout.For(manifest.ShareId).Manifest);
        BlobDownloadResult stored = (await blob.DownloadContentAsync(Ct)).Value;
        stored.Details.ContentType.ShouldBe("application/json");
        string json = stored.Content.ToString();
        json.ShouldStartWith("{\n  \"formatVersion\": 1,");
        json.ShouldContain($"\"shareId\": \"{manifest.ShareId}\"");
    }

    [Fact]
    public async Task ADamagedManifestBlob_Should_BeAnInvalidManifest_NotAMissingShare()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        await AzureStores.Container(azurite.ConnectionString, store).GetBlobClient(ShareLayout.For(shareId).Manifest)
            .UploadAsync(BinaryData.FromString("{ not json"), Ct);

        (await store.GetManifestAsync(shareId, Ct)).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public async Task StrayBlobs_Should_BeIgnored_ByObjectAndHistoryListings()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        var layout = ShareLayout.For(shareId);
        BlobContainerClient container = AzureStores.Container(azurite.ConnectionString, store);
        ContentHash real = ManifestBuilder.HashOf("real");
        await store.UploadObjectAsync(shareId, real, ObjectUpload.Raw(new MemoryStream("real"u8.ToArray()), 4), Ct);
        foreach (string stray in (string[])[$"{layout.ObjectsPrefix}ab/not-a-hash", $"{layout.ObjectsPrefix}readme.txt", $"{layout.ManifestHistoryPrefix}latest.json", $"{layout.ManifestHistoryPrefix}17.json"])
        {
            await container.GetBlobClient(stray).UploadAsync(BinaryData.FromString("stray"), Ct);
        }

        (await ToListAsync(store.ListObjectsAsync(shareId, Ct))).Select(item => item.Value.Hash).ShouldBe([real]);
        (await ToListAsync(store.ListManifestHistoryAsync(shareId, Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ADownloadThatFailsMidStream_Should_SurfaceAsAnIOException_WithoutSdkText()
    {
        AzureBlobShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var hash = ContentHash.FromBytes(SHA256.HashData(content));
        await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct);

        await using StoredObject stored = (await store.OpenObjectReadAsync(shareId, hash, Ct)).Value;
        Stream read = stored.Content;
        byte[] buffer = new byte[1024 * 1024];
        await read.ReadExactlyAsync(buffer, Ct); // the first buffered range
        await ObjectBlob(store, shareId, hash).DeleteAsync(cancellationToken: Ct);

        IOException failure = await Should.ThrowAsync<IOException>(async () => await read.ReadExactlyAsync(buffer, Ct));
        failure.Message.ShouldStartWith($"downloading '{ShareLayout.For(shareId).ObjectBlob(hash)}' from storage failed (Storage.");
        failure.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task GetLocation_Should_NameTheAccountFromTheConnectionString_AndNoSecret()
    {
        AzureBlobShareStore store = AzureStores.Create(azurite.ConnectionString, "team-share");

        StorageLocation location = store.GetLocation().Value;

        location.ShouldBe(new StorageLocation("azure-blob", azurite.AccountName, "team-share"));
    }

    [Fact]
    public async Task WithoutAConnectionString_EveryCall_Should_FailWithMissingConfiguration()
    {
        AzureBlobShareStore store = AzureStores.Create(connectionString: "");

        store.GetLocation().Error.ShouldBe(Domain.Configuration.ConfigurationErrors.Missing("AZURE_STORAGE_CONNECTION_STRING"));
        (await store.EnsureReadyForNewShareAsync(Ct)).Error.Code.ShouldBe("Configuration.Missing");
        (await store.GetManifestAsync(ShareId.New(), Ct)).Error.Code.ShouldBe("Configuration.Missing");
        (await ToListAsync(store.ListObjectsAsync(ShareId.New(), Ct))).Single().Error.Code.ShouldBe("Configuration.Missing");
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("Upper-Case")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("under_score")]
    public void AnInvalidContainerName_Should_BeAConfigurationError_NamingTheVariable(string container)
    {
        Error error = AzureStores.Create(azurite.ConnectionString, container).GetLocation().Error;

        error.Code.ShouldBe("Configuration.Invalid");
        error.Description.ShouldContain("MYSYNC_CONTAINER");
    }

    [Fact]
    public void AMalformedConnectionString_Should_BeAConfigurationError_ThatDoesNotEchoIt()
    {
        const string Malformed = "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=c2VjcmV0LWtleQ==;BlobEndpoint=::not a uri::";

        Error error = AzureStores.Create(Malformed).GetLocation().Error;

        error.Code.ShouldBe("Configuration.Invalid");
        error.Description.ShouldContain("AZURE_STORAGE_CONNECTION_STRING");
        error.Description.ShouldNotContain("c2VjcmV0LWtleQ");
        error.Description.ShouldNotContain("AccountName=acct");
    }

    [Fact]
    public async Task AWrongKey_Should_BeUnauthorized_AndNoErrorOrLogLineShouldCarryTheKey()
    {
        string wrongKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        string connectionString = WithAccountKey(azurite.ConnectionString, wrongKey);
        RecordingLogger logger = new();
        AzureBlobShareStore store = AzureStores.Create(connectionString, logger: logger);

        Result ready = await store.EnsureReadyForNewShareAsync(Ct);
        Result<ManifestSnapshot?> read = await store.GetManifestAsync(ShareId.New(), Ct);

        ready.Error.ShouldBe(StorageErrors.Unauthorized);
        read.Error.ShouldBe(StorageErrors.Unauthorized);
        logger.Lines.ShouldNotBeEmpty();
        foreach (string text in logger.Lines.Append(ready.Error.Description).Append(read.Error.Description))
        {
            text.ShouldNotContain(wrongKey);
            text.ShouldNotContain(connectionString);
        }
    }

    [Fact]
    public async Task AnEndpointNobodyListensOn_Should_BeUnreachable_AndQuickly()
    {
        RecordingLogger logger = new();
        AzureBlobShareStore store = AzureStores.Create(
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;",
            settings: AzureStores.TestSettings with { MaxRetries = 0 },
            logger: logger);

        Result<ManifestSnapshot?> read = await store.GetManifestAsync(ShareId.New(), Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        read.Error.ShouldBe(StorageErrors.Unreachable);
        logger.Lines.ShouldAllBe(line => !line.Contains("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq", StringComparison.Ordinal));
    }

    private async Task<AzureBlobShareStore> ReadyStoreAsync()
    {
        AzureBlobShareStore store = AzureStores.Create(azurite.ConnectionString);
        (await store.EnsureReadyForNewShareAsync(Ct)).IsSuccess.ShouldBeTrue();
        return store;
    }

    private BlockBlobClient ObjectBlob(AzureBlobShareStore store, ShareId shareId, ContentHash hash) =>
        AzureStores.Container(azurite.ConnectionString, store).GetBlockBlobClient(ShareLayout.For(shareId).ObjectBlob(hash));

    private static string WithAccountKey(string connectionString, string key) =>
        string.Join(';', connectionString.Split(';').Select(part =>
            part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase) ? $"AccountKey={key}" : part));

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> items)
    {
        List<T> list = [];
        await foreach (T item in items)
        {
            list.Add(item);
        }

        return list;
    }
}
