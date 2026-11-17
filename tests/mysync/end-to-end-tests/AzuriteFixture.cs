using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MySync.EndToEndTests;
using MySync.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(AzuriteFixture))]

namespace MySync.EndToEndTests;

/// <summary>One Azurite for the whole assembly. Inject it into a test class's constructor.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private AzuriteEnvironment? _environment;

    public string ConnectionString =>
        _environment?.ConnectionString ?? throw new InvalidOperationException("Azurite has not started.");

    /// <summary>The storage account's name: <c>devstoreaccount1</c> on Azurite, the real one in the real-Azure workflow.</summary>
    public string AccountName => AzuriteEnvironment.AccountNameOf(ConnectionString);

    public async ValueTask InitializeAsync() =>
        _environment = await AzuriteEnvironment.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_environment is null)
        {
            return;
        }

        // Supplied storage (a local Azurite, or a real account) outlives the run: remove this run's containers.
        if (_environment.IsExternal)
        {
            BlobServiceClient service = new(_environment.ConnectionString);
            await foreach (BlobContainerItem container in service.GetBlobContainersAsync(BlobContainerTraits.None, BlobContainerStates.None, AzuriteEnvironment.RunPrefix, CancellationToken.None))
            {
                await service.DeleteBlobContainerAsync(container.Name, cancellationToken: CancellationToken.None);
            }
        }

        await _environment.DisposeAsync();
    }
}
