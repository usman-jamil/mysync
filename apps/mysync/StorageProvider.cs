using Microsoft.Extensions.Configuration;

namespace MySync.Cli;

/// <summary>
/// Which <c>IShareStore</c> (and which <c>IPublishTarget</c>) implementation the composition root
/// registers: <c>MYSYNC_PROVIDER</c> for shares, <c>MYSYNC_PUBLISH_PROVIDER</c> (default: the
/// share's) for publications.
/// </summary>
internal enum StorageProvider
{
    Unknown = 0,
    AzureBlob = 1,
    S3 = 2,
}

internal static class StorageProviderResolver
{
    public const string Variable = "MYSYNC_PROVIDER";
    public const string PublishVariable = "MYSYNC_PUBLISH_PROVIDER";
    public const string AzureBlobName = "azure-blob";
    public const string S3Name = "s3";

    /// <summary><c>MYSYNC_PROVIDER</c>; unset means <c>azure-blob</c>.</summary>
    public static StorageProvider Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Variable]) ?? StorageProvider.AzureBlob;
    }

    /// <summary><c>MYSYNC_PUBLISH_PROVIDER</c>; unset means the share's provider.</summary>
    public static StorageProvider ResolvePublishing(IConfiguration configuration, StorageProvider shares)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[PublishVariable]) ?? shares;
    }

    public static string NameOf(StorageProvider provider) => provider switch
    {
        StorageProvider.AzureBlob => AzureBlobName,
        StorageProvider.S3 => S3Name,
        _ => "unknown",
    };

    private static StorageProvider? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        if (string.Equals(trimmed, AzureBlobName, StringComparison.OrdinalIgnoreCase))
        {
            return StorageProvider.AzureBlob;
        }

        return string.Equals(trimmed, S3Name, StringComparison.OrdinalIgnoreCase) ? StorageProvider.S3 : StorageProvider.Unknown;
    }
}
