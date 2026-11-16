using Azure.Storage.Blobs;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>
/// Proves the Azurite wiring works. The <c>IShareStore</c> contract suite replaces this once
/// <c>AzureBlobShareStore</c> exists.
/// </summary>
public class AzuriteSmokeTests(AzuriteFixture azurite)
{
    [Fact]
    public async Task Azurite_Should_CreateAnIsolatedContainer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var container = new BlobContainerClient(azurite.ConnectionString, AzuriteEnvironment.NewContainerName());

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        bool exists = await container.ExistsAsync(cancellationToken);
        exists.ShouldBeTrue();
    }
}
