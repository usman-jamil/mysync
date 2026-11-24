using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MySync.Testing;

/// <summary>
/// A real, test-only Azure storage account (and optionally a second one with static website hosting
/// for the publishing tests). Each test isolates itself in its own blob container
/// (<see cref="NewContainerName"/>), named with this run's prefix so <see cref="CleanUpAsync"/> can
/// delete them all at the end.
/// </summary>
/// <remarks>Use an account that holds nothing but test data: tests create and delete containers in it.</remarks>
public sealed class AzureTestStorage
{
    /// <summary>An <b>account-key</b> connection string (the wrong-key and SAS tests need the key).</summary>
    public const string ConnectionStringVariable = "MYSYNC_TEST_AZURE_CONNECTION_STRING";

    /// <summary>The publishing account's account-key connection string: static website hosting on, index document <c>index.html</c>.</summary>
    public const string PublishConnectionStringVariable = "MYSYNC_TEST_AZURE_PUBLISH_CONNECTION_STRING";

    /// <summary>That account's static website address, e.g. <c>https://&lt;account&gt;.z13.web.core.windows.net</c>.</summary>
    public const string PublishBaseUrlVariable = "MYSYNC_TEST_AZURE_PUBLISH_BASE_URL";

    private readonly string? _connectionString;
    private readonly string? _publishConnectionString;
    private readonly string? _publishBaseUrl;

    private AzureTestStorage(string? connectionString, string? publishConnectionString, string? publishBaseUrl)
    {
        _connectionString = connectionString;
        _publishConnectionString = publishConnectionString;
        _publishBaseUrl = publishBaseUrl;
    }

    public static AzureTestStorage FromEnvironment() => new(
        TestStorage.Variable(ConnectionStringVariable),
        TestStorage.Variable(PublishConnectionStringVariable),
        TestStorage.Variable(PublishBaseUrlVariable)?.TrimEnd('/'));

    public bool IsConfigured => _connectionString is not null;

    public bool HasPublishing => _publishConnectionString is not null && _publishBaseUrl is not null;

    /// <summary>The share account's connection string; skips (or fails) the test when none is configured.</summary>
    public string ConnectionString => _connectionString ?? TestStorage.Unavailable<string>("Azure test storage", ConnectionStringVariable);

    public string PublishConnectionString =>
        HasPublishing ? _publishConnectionString! : TestStorage.Unavailable<string>("An Azure publishing account", PublishConnectionStringVariable, PublishBaseUrlVariable);

    public string PublishBaseUrl =>
        HasPublishing ? _publishBaseUrl! : TestStorage.Unavailable<string>("An Azure publishing account", PublishConnectionStringVariable, PublishBaseUrlVariable);

    /// <summary>The account the connection string names.</summary>
    public string AccountName => AccountNameOf(ConnectionString);

    /// <summary>The account key in the connection string, to check no output ever carries it.</summary>
    public string AccountKey => KeyOf(ConnectionString);

    /// <summary><see cref="TestStorage.RunPrefix"/> plus 24 hex characters: 35 characters, a valid and unique container name.</summary>
    public static string NewContainerName() =>
        TestStorage.RunPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24];

    public static string AccountNameOf(string connectionString) => PartOf(connectionString, "AccountName")
        ?? throw new ArgumentException("The test connection string must name its account (AccountName=...).", nameof(connectionString));

    public static string KeyOf(string connectionString) => PartOf(connectionString, "AccountKey")
        ?? throw new ArgumentException("The test connection string must be an account-key one (AccountKey=...).", nameof(connectionString));

    /// <summary>Deletes every container this run created, and every publication blob written since it started.</summary>
    public async Task CleanUpAsync()
    {
        if (_connectionString is not null)
        {
            BlobServiceClient service = new(_connectionString);
            await foreach (BlobContainerItem container in service.GetBlobContainersAsync(BlobContainerTraits.None, BlobContainerStates.None, TestStorage.RunPrefix, CancellationToken.None))
            {
                await service.DeleteBlobContainerAsync(container.Name, cancellationToken: CancellationToken.None);
            }
        }

        if (_publishConnectionString is not null)
        {
            BlobContainerClient web = new BlobServiceClient(_publishConnectionString).GetBlobContainerClient("$web");
            await foreach (BlobItem item in web.GetBlobsAsync(BlobTraits.None, BlobStates.None, "p/", CancellationToken.None))
            {
                if (item.Properties.LastModified >= TestStorage.RunStartedUtc)
                {
                    await web.DeleteBlobIfExistsAsync(item.Name, cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    private static string? PartOf(string connectionString, string name)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return part[(name.Length + 1)..];
            }
        }

        return null;
    }
}
