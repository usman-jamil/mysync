using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.IntegrationTests;

/// <summary>What only the S3 stores do, beyond the shared contracts, on real S3 (CLAUDE.md §11, "S3 differences").</summary>
public sealed class S3ShareStoreTests(S3StorageFixture s3)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFileLargerThanAPart_Should_BeAMultipartUpload_AndReadBackByteForByte()
    {
        S3ShareStore store = S3Stores.ShareStore(s3.Storage);
        var shareId = ShareId.New();
        byte[] content = RandomNumberGenerator.GetBytes(11 * 1024 * 1024 + 123); // three 5 MiB parts
        var hash = ContentHash.FromBytes(SHA256.HashData(content));

        (await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        GetObjectMetadataResponse head = await HeadAsync(store, ShareLayout.For(shareId).ObjectBlob(hash));
        head.ETag.ShouldEndWith("-3\"", customMessage: "a multipart object's ETag counts its parts");
        head.Metadata["x-amz-meta-sha256"].ShouldBe(hash.Value);
        head.Metadata["x-amz-meta-size"].ShouldBe(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await ReadAllAsync(store, shareId, hash)).ShouldBe(content);

        // Uploaded again: already stored, nothing sent.
        (await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AGzipMultipartUpload_Should_CarryContentEncoding_AndReadBackAsStored()
    {
        S3ShareStore store = S3Stores.ShareStore(s3.Storage);
        var shareId = ShareId.New();
        byte[] stored = RandomNumberGenerator.GetBytes(6 * 1024 * 1024); // gzip-framed or not, the store doesn't look
        ContentHash hash = ManifestBuilder.HashOf("the uncompressed content");

        (await store.UploadObjectAsync(shareId, hash, new ObjectUpload(new MemoryStream(stored), ObjectEncoding.Gzip, 9_000_000, () => Result.Success()), Ct)).IsSuccess.ShouldBeTrue();

        GetObjectMetadataResponse head = await HeadAsync(store, ShareLayout.For(shareId).ObjectBlob(hash));
        S3ShareStore.EncodingOf(head.Headers.ContentEncoding).Value.ShouldBe(ObjectEncoding.Gzip);
        await using StoredObject read = (await store.OpenObjectReadAsync(shareId, hash, Ct)).Value;
        read.Encoding.ShouldBe(ObjectEncoding.Gzip);
        read.StoredLength.ShouldBe(stored.Length);
        using MemoryStream copy = new();
        await read.Content.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(stored, "the stored bytes, never decoded by the SDK");
    }

    [Fact]
    public async Task IdenticalBytes_Should_KeepTheirETag_OnS3()
    {
        S3ShareStore store = S3Stores.ShareStore(s3.Storage);
        Manifest manifest = new ManifestBuilder().WithFile("a.txt", "a").Build();
        string first = (await store.CreateManifestAsync(manifest.ShareId, manifest, Ct)).Value;

        string second = (await store.ReplaceManifestAsync(manifest.ShareId, manifest, first, Ct)).Value;

        second.ShouldBe(first, "S3's ETag follows the bytes: why every commit must write new bytes (a new revision)");
    }

    [Fact]
    public async Task AWrongRegion_Should_BeAConfigurationError_NamingTheBucketsRegion()
    {
        string wrong = s3.Storage.Region == "eu-west-1" ? "us-east-2" : "eu-west-1";
        string bucket = s3.Storage.Bucket;
        S3Connection connection = new(
            () => S3Bucket.From(bucket, wrong, S3TestStorage.NewKeyPrefix(), S3StorageOptions.BucketVariable, S3StorageOptions.RegionVariable),
            profile: null,
            S3Stores.TestSettings,
            logger: null);
        S3ShareStore store = new(connection, S3Stores.Serializer, S3Stores.RegistrySerializer, TimeProvider.System, options: null, logger: null);

        Error error = (await store.GetManifestAsync(ShareId.New(), Ct)).Error;

        error.Code.ShouldBe("Configuration.WrongRegion");
        error.Description.ShouldContain(bucket);
        error.Description.ShouldContain(wrong);
        (await store.EnsureReadyForNewShareAsync(Ct)).Error.Code.ShouldBe("Configuration.WrongRegion");
    }

    [Fact]
    public async Task ABucketThatDoesntExist_Should_NameItAndTheRegion()
    {
        string missing = "mysync-missing-" + Guid.NewGuid().ToString("N")[..20];
        string region = s3.Storage.Region;
        S3Connection connection = new(
            () => S3Bucket.From(missing, region, string.Empty, S3StorageOptions.BucketVariable, S3StorageOptions.RegionVariable),
            profile: null,
            S3Stores.TestSettings,
            logger: null);
        S3ShareStore store = new(connection, S3Stores.Serializer, S3Stores.RegistrySerializer, TimeProvider.System, options: null, logger: null);

        Error error = (await store.GetManifestAsync(ShareId.New(), Ct)).Error;

        // Without s3:ListBucket on a bucket that isn't ours, S3 may say AccessDenied instead: never NotFound.
        error.Code.ShouldBeOneOf("Configuration.BucketNotFound", "Storage.Unauthorized");
    }

    [Fact]
    public async Task WrongKeys_Should_BeUnauthorized_AndNoErrorOrLogLineCarryTheSecret()
    {
        const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(30));
        RecordingLogger logger = new();
        S3ShareStore store = new(S3Stores.WithCredentials(s3.Storage, AccessKey, secret, logger), S3Stores.Serializer, S3Stores.RegistrySerializer, TimeProvider.System, options: null, logger);

        Error error = (await store.GetManifestAsync(ShareId.New(), Ct)).Error;

        error.Code.ShouldBe("Storage.Unauthorized");
        foreach (string text in logger.Lines.Append(error.Description))
        {
            text.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task APresignedCopySource_Should_BeReadableWithoutCredentials_AndNameTheObjectNatively()
    {
        S3ShareStore store = S3Stores.ShareStore(s3.Storage);
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("published content");
        ContentHash hash = ManifestBuilder.HashOf(content);
        (await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        CopySource source = (await store.GetCopySourceAsync(shareId, hash, TimeSpan.FromMinutes(5), Ct)).Value!;

        source.Native!.Container.ShouldBe(s3.Storage.Bucket);
        source.Native.Key.ShouldBe(S3Stores.PrefixOf(store) + ShareLayout.For(shareId).ObjectBlob(hash));
        using HttpClient anonymous = new();
        (await anonymous.GetByteArrayAsync(source.Url, Ct)).ShouldBe(content);
    }

    [Fact]
    public async Task AnAliasWithoutItsMetadata_Should_ListAsInvalid_AndAStrayKeyAsStray()
    {
        string prefix = S3TestStorage.NewKeyPrefix();
        S3AliasStore store = S3Stores.AliasStore(s3.Storage, prefix: prefix);
        using AmazonS3Client client = s3.Storage.Client();
        await client.PutObjectAsync(new PutObjectRequest { BucketName = s3.Storage.Bucket, Key = prefix + "aliases/damaged", ContentBody = "{}" }, Ct);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = s3.Storage.Bucket, Key = prefix + "aliases/Not_A_Name", ContentBody = "{}" }, Ct);

        List<string> listed = [];
        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            listed.Add(item.IsSuccess ? item.Value.Name.Value : item.Error.Code);
        }

        listed.ShouldBe(["Storage.StrayAlias", "Storage.InvalidAlias"]); // listing order: "N" sorts before "d"
    }

    [Fact]
    public async Task AnAlias_Should_HaveTheSameBodyAsOnAzure()
    {
        string prefix = S3TestStorage.NewKeyPrefix();
        S3AliasStore store = S3Stores.AliasStore(s3.Storage, prefix: prefix);
        var shareId = ShareId.New();
        (await store.CreateAsync(new AliasRecord(ShareName.Create("same-shape").Value, shareId, new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), "me@HOST"), Ct)).IsSuccess.ShouldBeTrue();

        using AmazonS3Client client = s3.Storage.Client();
        using GetObjectResponse response = await client.GetObjectAsync(s3.Storage.Bucket, prefix + "aliases/same-shape", Ct);
        using StreamReader reader = new(response.ResponseStream);
        (await reader.ReadToEndAsync(Ct)).ShouldBe($"{{\"shareId\":\"{shareId}\",\"createdUtc\":\"2026-11-24T09:00:00+00:00\",\"createdBy\":\"me@HOST\"}}");
        response.Headers.ContentType.ShouldBe("application/json");
    }

    private async Task<GetObjectMetadataResponse> HeadAsync(S3ShareStore store, string name)
    {
        using AmazonS3Client client = s3.Storage.Client();
        return await client.GetObjectMetadataAsync(s3.Storage.Bucket, S3Stores.PrefixOf(store) + name, Ct);
    }

    private static async Task<byte[]> ReadAllAsync(S3ShareStore store, ShareId shareId, ContentHash hash)
    {
        await using StoredObject stored = (await store.OpenObjectReadAsync(shareId, hash, Ct)).Value;
        using MemoryStream copy = new();
        await stored.Content.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }
}
