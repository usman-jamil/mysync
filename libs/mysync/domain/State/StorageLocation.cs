namespace MySync.Domain.State;

/// <summary>
/// Where a share lives: provider, account and container. Recorded in <c>.mysynctool</c> (never a
/// secret) so a folder is never synced against a different account or container (CLAUDE.md §5).
/// </summary>
public sealed record StorageLocation(string Provider, string AccountName, string Container)
{
    /// <summary>
    /// Provider compares ordinally; account and container names ignore case (Azure treats them
    /// case-insensitively and requires lowercase anyway).
    /// </summary>
    public bool IsSameAs(StorageLocation other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Provider, other.Provider, StringComparison.Ordinal)
            && string.Equals(AccountName, other.AccountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Container, other.Container, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Provider}:{AccountName}/{Container}";

    /// <summary>For people: <c>azure-blob (account teamsync, container mysync)</c>, <c>s3 (bucket team-shares in eu-west-1)</c>.</summary>
    public string Describe() => Provider switch
    {
        "s3" => $"s3 (bucket {Container} in {AccountName})",
        "azure-blob" => $"azure-blob (account {AccountName}, container {Container})",
        _ => ToString(),
    };

    /// <summary>The environment variables that choose this provider's location (no values: they may be secrets).</summary>
    public static string SettingsOf(string provider) => provider switch
    {
        "s3" => "MYSYNC_S3_BUCKET, MYSYNC_S3_REGION",
        "azure-blob" => "AZURE_STORAGE_CONNECTION_STRING, MYSYNC_CONTAINER",
        _ => "MYSYNC_PROVIDER",
    };
}
