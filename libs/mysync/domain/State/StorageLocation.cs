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
}
