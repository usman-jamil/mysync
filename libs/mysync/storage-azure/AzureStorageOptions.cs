namespace MySync.Storage.Azure;

/// <summary>
/// Bound from environment variables only (CLAUDE.md §5). Not validated at startup, so
/// <c>mysync --help</c> works with nothing configured; a missing value surfaces as
/// <c>ConfigurationErrors.Missing</c> when a command first needs storage.
/// </summary>
public sealed class AzureStorageOptions
{
    public const string ConnectionStringVariable = "AZURE_STORAGE_CONNECTION_STRING";
    public const string ContainerVariable = "MYSYNC_CONTAINER";
    public const string DefaultContainer = "mysync";

    /// <summary>
    /// A secret. Never print, log or store it.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string Container { get; set; } = DefaultContainer;

    /// <summary>
    /// Keeps the connection string out of anything that formats this object.
    /// </summary>
    public override string ToString() => $"{nameof(AzureStorageOptions)} {{ Container = {Container} }}";
}
