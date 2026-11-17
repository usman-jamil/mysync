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
/// Set <see cref="ConnectionStringVariable"/> to use storage that is already running instead: a
/// local Azurite (no Docker needed) or a real, test-only storage account (the manually triggered
/// real-Azure workflow does this). Then every container a run creates starts with
/// <see cref="RunPrefix"/>, so the fixtures can delete them afterwards. The developer's real
/// <c>AZURE_STORAGE_CONNECTION_STRING</c> is never read.
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

    /// <summary>True when the storage was supplied (<see cref="ConnectionStringVariable"/>), so the run must clean up after itself.</summary>
    public bool IsExternal => _container is null;

    /// <summary>The account the connection string names (<c>devstoreaccount1</c> for Azurite).</summary>
    public string AccountName => AccountNameOf(ConnectionString);

    /// <summary>Starts every container this test run creates: <c>t-&lt;8 hex&gt;-</c>.</summary>
    public static string RunPrefix { get; } = "t-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + "-";

    public static string AccountNameOf(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase))
            {
                return part["AccountName=".Length..];
            }

            if (part.Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            {
                return "devstoreaccount1";
            }
        }

        throw new ArgumentException("The test connection string must name its account (AccountName=...).", nameof(connectionString));
    }

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
    /// <see cref="RunPrefix"/> plus 24 hex characters: lowercase, 35 characters, a valid and unique blob container name.
    /// </summary>
    public static string NewContainerName() =>
        RunPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24];

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
