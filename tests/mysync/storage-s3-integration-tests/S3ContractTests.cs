using Amazon.S3;
using Amazon.S3.Model;
using MySync.Application.Abstractions.Storage;
using MySync.Testing.Contracts;
using Xunit;

namespace MySync.Storage.S3.IntegrationTests;

/// <summary>
/// The shared <see cref="IShareStore"/> contract against the real test bucket, unchanged: the same
/// suite runs against <c>InMemoryShareStore</c> and Azure. If S3 and the fake disagree, the fake is wrong.
/// </summary>
public sealed class S3ShareStoreContractTests(S3StorageFixture s3) : ShareStoreContractTests
{
    protected override Task<IShareStore> CreateStoreAsync() => Task.FromResult<IShareStore>(S3Stores.ShareStore(s3.Storage));

    protected override async Task PlantObjectAsync(IShareStore store, Domain.Shares.ShareId shareId, Domain.Content.ContentHash hash, byte[] bytes, string contentEncoding)
    {
        using AmazonS3Client client = s3.Storage.Client();
        PutObjectRequest request = new()
        {
            BucketName = s3.Storage.Bucket,
            Key = S3Stores.PrefixOf(store) + Domain.Storage.ShareLayout.For(shareId).ObjectBlob(hash),
            InputStream = new MemoryStream(bytes),
            ContentType = "application/octet-stream",
        };
        request.Headers.ContentEncoding = contentEncoding;
        await client.PutObjectAsync(request, TestContext.Current.CancellationToken);
    }
}

/// <summary>The shared <see cref="IAliasStore"/> contract (with its S3 ETag case) against the real test bucket.</summary>
public sealed class S3AliasStoreContractTests(S3StorageFixture s3) : AliasStoreContractTests
{
    protected override Task<IAliasStore> CreateStoreAsync(int? pageSize = null) => Task.FromResult<IAliasStore>(S3Stores.AliasStore(s3.Storage, pageSize));
}
