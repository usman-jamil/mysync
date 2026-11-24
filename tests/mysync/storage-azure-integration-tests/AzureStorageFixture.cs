using MySync.Storage.Azure.IntegrationTests;
using MySync.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(AzureStorageFixture))]

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>
/// The real, test-only Azure storage (CLAUDE.md §14) for the whole assembly; each test makes its
/// own container. A test that needs storage skips without it (fails in CI, where
/// <c>MYSYNC_TEST_REQUIRE_STORAGE</c> is set). Removes what this run wrote at the end.
/// </summary>
public sealed class AzureStorageFixture : IAsyncLifetime
{
    public AzureTestStorage Storage { get; } = AzureTestStorage.FromEnvironment();

    public string ConnectionString => Storage.ConnectionString;

    public string AccountName => Storage.AccountName;

    public string AccountKey => Storage.AccountKey;

    public string PublishConnectionString => Storage.PublishConnectionString;

    public string PublishBaseUrl => Storage.PublishBaseUrl;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await Storage.CleanUpAsync();
}
