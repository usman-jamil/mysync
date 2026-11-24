using MySync.Application.Abstractions.Storage;
using MySync.Testing.Contracts;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>
/// The shared <see cref="IShareStore"/> contract against real Azure storage. The same suite runs against
/// <c>InMemoryShareStore</c> in the application tests: if the two disagree, the fake is wrong.
/// </summary>
public sealed class AzureShareStoreContractTests(AzureStorageFixture azure) : ShareStoreContractTests
{
    protected override Task<IShareStore> CreateStoreAsync() => Task.FromResult<IShareStore>(AzureStores.Create(azure.ConnectionString));

    protected override async Task PlantObjectAsync(IShareStore store, Domain.Shares.ShareId shareId, Domain.Content.ContentHash hash, byte[] bytes, string contentEncoding)
    {
        global::Azure.Storage.Blobs.BlobContainerClient container = AzureStores.Container(azure.ConnectionString, (AzureBlobShareStore)store);
        await container.GetBlobClient(Domain.Storage.ShareLayout.For(shareId).ObjectBlob(hash)).UploadAsync(
            BinaryData.FromBytes(bytes),
            new global::Azure.Storage.Blobs.Models.BlobUploadOptions { HttpHeaders = new global::Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentEncoding = contentEncoding } },
            Xunit.TestContext.Current.CancellationToken);
    }
}
