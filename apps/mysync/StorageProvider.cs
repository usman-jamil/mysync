using Microsoft.Extensions.Configuration;

namespace MySync.Cli;

/// <summary>
/// Which <c>IShareStore</c> implementation the composition root registers, from
/// <c>MYSYNC_PROVIDER</c>.
/// </summary>
internal enum StorageProvider
{
    Unknown = 0,
    AzureBlob = 1,
}

internal static class StorageProviderResolver
{
    public const string Variable = "MYSYNC_PROVIDER";
    public const string AzureBlobName = "azure-blob";

    public static StorageProvider Resolve(IConfiguration configuration)
    {
        string? name = configuration[Variable];

        return string.IsNullOrWhiteSpace(name) || string.Equals(name, AzureBlobName, StringComparison.OrdinalIgnoreCase)
            ? StorageProvider.AzureBlob
            : StorageProvider.Unknown;
    }
}
