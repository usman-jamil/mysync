using MySync.EndToEndTests;
using MySync.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(StorageFixture))]

namespace MySync.EndToEndTests;

/// <summary>
/// The real test storage for the whole assembly (CLAUDE.md §14): a test-only Azure account and a
/// test-only S3 bucket, each with an optional publishing side. A test that needs one skips without
/// it (fails in CI, where <c>MYSYNC_TEST_REQUIRE_STORAGE</c> is set). Removes what this run wrote.
/// </summary>
public sealed class StorageFixture : IAsyncLifetime
{
    public AzureTestStorage AzureStorage { get; } = AzureTestStorage.FromEnvironment();

    public S3TestStorage S3Storage { get; } = S3TestStorage.FromEnvironment();

    public Backend Azure => new AzureBackend(AzureStorage);

    public Backend S3 => new S3Backend(S3Storage);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await AzureStorage.CleanUpAsync();
        await S3Storage.CleanUpAsync();
    }
}
