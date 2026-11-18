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

    /// <summary>
    /// With <see cref="ConnectionStringVariable"/>: a publishing account (static website hosting on,
    /// index document <c>index.html</c>) for the publishing tests. Without it they skip.
    /// </summary>
    public const string PublishConnectionStringVariable = "MYSYNC_TEST_PUBLISH_CONNECTION_STRING";

    /// <summary>Azurite's well-known key, used for every account it starts.</summary>
    public const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>The accounts the started Azurite has besides <c>devstoreaccount1</c> (publishing tests).</summary>
    public const string PublishAccount = "mysyncpublish";
    public const string NoWebsiteAccount = "mysyncnoweb";
    public const string WrongIndexAccount = "mysyncwrongindex";

    private readonly AzuriteContainer? _container;

    private AzuriteEnvironment(string connectionString, AzuriteContainer? container, string? publishConnectionString)
    {
        ConnectionString = connectionString;
        _container = container;
        PublishConnectionString = publishConnectionString;
    }

    public string ConnectionString { get; }

    /// <summary>
    /// The publishing account: <see cref="PublishAccount"/> on the started Azurite (static website
    /// on), or <see cref="PublishConnectionStringVariable"/>'s. <c>null</c> when supplied storage has none.
    /// </summary>
    public string? PublishConnectionString { get; }

    /// <summary>Another account of the started Azurite (only when <see cref="IsExternal"/> is false).</summary>
    public string AccountConnectionString(string account)
    {
        if (_container is null)
        {
            throw new InvalidOperationException("Only the Azurite this run started has the extra accounts.");
        }

        return ForAccount(ConnectionString, account);
    }

    private static string ForAccount(string connectionString, string account) => connectionString
        .Replace("AccountName=devstoreaccount1", "AccountName=" + account, StringComparison.Ordinal)
        .Replace("/devstoreaccount1", "/" + account, StringComparison.Ordinal);

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
            string? publish = Environment.GetEnvironmentVariable(PublishConnectionStringVariable);
            return new AzuriteEnvironment(external, container: null, string.IsNullOrWhiteSpace(publish) ? null : publish);
        }

        // devstoreaccount1 for shares, plus publishing accounts. Static website settings are
        // account-wide, so each state the tests need has its own account.
        string accounts = string.Join(';', new[] { "devstoreaccount1", PublishAccount, NoWebsiteAccount, WrongIndexAccount }.Select(account => $"{account}:{AzuriteKey}"));
        AzuriteContainer container = new AzuriteBuilder(AzuriteImage.Name)
            .WithCommand("--skipApiVersionCheck")
            .WithInMemoryPersistence()
            .WithEnvironment("AZURITE_ACCOUNTS", accounts)
            .Build();

        await container.StartAsync(cancellationToken);

        string connectionString = container.GetConnectionString();
        await SetUpStaticWebsiteAsync(ForAccount(connectionString, PublishAccount), "index.html", cancellationToken);
        await SetUpStaticWebsiteAsync(ForAccount(connectionString, WrongIndexAccount), "home.html", cancellationToken);
        return new AzuriteEnvironment(connectionString, container, ForAccount(connectionString, PublishAccount));
    }

    // What `az storage blob service-properties update --static-website` does, plus the $web container it creates.
    private static async Task SetUpStaticWebsiteAsync(string connectionString, string indexDocument, CancellationToken cancellationToken)
    {
        Azure.Storage.Blobs.BlobServiceClient service = new(connectionString);
        Azure.Storage.Blobs.Models.BlobServiceProperties properties = (await service.GetPropertiesAsync(cancellationToken)).Value;
        properties.StaticWebsite = new Azure.Storage.Blobs.Models.BlobStaticWebsite { Enabled = true, IndexDocument = indexDocument, ErrorDocument404Path = "404.html" };
        await service.SetPropertiesAsync(properties, cancellationToken);
        await service.GetBlobContainerClient("$web").CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// <see cref="RunPrefix"/> plus 24 hex characters: lowercase, 35 characters, a valid and unique blob container name.
    /// </summary>
    public static string NewContainerName() =>
        RunPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24];

    /// <summary>When this run started: supplied storage keeps publications, so the fixtures remove the ones this run made.</summary>
    public static DateTimeOffset RunStartedUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>
    /// On supplied storage: deletes every publication blob (<c>$web/p/…</c>) written since this run
    /// started, so no test publication stays public on a real account.
    /// </summary>
    public async Task RemoveThisRunsPublicationsAsync()
    {
        if (!IsExternal || PublishConnectionString is null)
        {
            return;
        }

        Azure.Storage.Blobs.BlobContainerClient web = new Azure.Storage.Blobs.BlobServiceClient(PublishConnectionString).GetBlobContainerClient("$web");
        await foreach (Azure.Storage.Blobs.Models.BlobItem item in web.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "p/", CancellationToken.None))
        {
            if (item.Properties.LastModified >= RunStartedUtc)
            {
                await web.DeleteBlobIfExistsAsync(item.Name, cancellationToken: CancellationToken.None);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
