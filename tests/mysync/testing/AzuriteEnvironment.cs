using System.Globalization;
using Testcontainers.Azurite;

namespace MySync.Testing;

/// <summary>
/// One Azurite per test assembly, shared by every test in it. Each test isolates itself with
/// its own blob container (<see cref="NewContainerName"/>). Wrap it in an xUnit assembly
/// fixture in each Azurite-backed suite.
/// </summary>
/// <remarks>
/// Azurite is started with <c>--skipApiVersionCheck</c> so a newer Azure SDK's
/// <c>x-ms-version</c> header is accepted by the pinned image.
/// Set <see cref="ConnectionStringVariable"/> to use an Azurite that is already running
/// (no Docker needed). The developer's real <c>AZURE_STORAGE_CONNECTION_STRING</c> is never read.
/// </remarks>
public sealed class AzuriteEnvironment : IAsyncDisposable
{
    public const string ConnectionStringVariable = "MYSYNC_TEST_AZURITE_CONNECTION_STRING";

    private readonly AzuriteContainer? _container;

    private AzuriteEnvironment(string connectionString, AzuriteContainer? container)
    {
        ConnectionString = connectionString;
        _container = container;
    }

    public string ConnectionString { get; }

    public static async Task<AzuriteEnvironment> StartAsync(CancellationToken cancellationToken = default)
    {
        string? external = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(external))
        {
            return new AzuriteEnvironment(external, container: null);
        }

        AzuriteContainer container = new AzuriteBuilder(AzuriteImage.Name)
            .WithCommand("--skipApiVersionCheck")
            .WithInMemoryPersistence()
            .Build();

        await container.StartAsync(cancellationToken);

        return new AzuriteEnvironment(container.GetConnectionString(), container);
    }

    /// <summary>
    /// <c>t-&lt;guid&gt;</c>: lowercase, 34 characters, a valid and unique blob container name.
    /// </summary>
    public static string NewContainerName() =>
        "t-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
