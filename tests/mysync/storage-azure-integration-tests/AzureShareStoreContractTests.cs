using MySync.Application.Abstractions.Storage;
using MySync.Testing.Contracts;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>
/// The shared <see cref="IShareStore"/> contract against Azurite. The same suite runs against
/// <c>InMemoryShareStore</c> in the application tests: if the two disagree, the fake is wrong.
/// </summary>
public sealed class AzureShareStoreContractTests(AzuriteFixture azurite) : ShareStoreContractTests
{
    protected override Task<IShareStore> CreateStoreAsync() => Task.FromResult<IShareStore>(AzureStores.Create(azurite.ConnectionString));
}
