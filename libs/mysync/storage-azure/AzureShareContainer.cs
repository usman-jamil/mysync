using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Storage.Blobs;
using MySync.Domain.Configuration;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Storage.Azure;

/// <summary>
/// The share container from <see cref="AzureStorageOptions"/>, shared by <see cref="AzureBlobShareStore"/>
/// and <see cref="AzureAliasStore"/>: same connection string, container name rules, client retries,
/// and configuration errors. Read on first use, never at startup.
/// </summary>
internal sealed partial record AzureShareContainer(BlobContainerClient Container, StorageLocation Location)
{
    public static Result<AzureShareContainer> Connect(AzureStorageOptions options, AzureTransferSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return Result.Failure<AzureShareContainer>(ConfigurationErrors.Missing(AzureStorageOptions.ConnectionStringVariable));
        }

        string container = options.Container;
        if (!ContainerName().IsMatch(container))
        {
            return Result.Failure<AzureShareContainer>(ConfigurationErrors.Invalid(
                AzureStorageOptions.ContainerVariable,
                $"'{container}' is not a container name (3-63 lowercase letters, digits and single hyphens)."));
        }

        BlobClientOptions clientOptions = new()
        {
            Retry =
            {
                Mode = RetryMode.Exponential,
                MaxRetries = settings.MaxRetries,
                Delay = TimeSpan.FromSeconds(0.8),
                MaxDelay = TimeSpan.FromSeconds(30),
                NetworkTimeout = settings.NetworkTimeout,
            },
        };

        try
        {
            BlobServiceClient service = new(options.ConnectionString, clientOptions);
            string account = string.IsNullOrEmpty(service.AccountName) ? service.Uri.Host : service.AccountName;
            return new AzureShareContainer(service.GetBlobContainerClient(container), new StorageLocation(AzureBlobShareStore.ProviderName, account, container));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            // The SDK's message may quote the connection string, so it is not used.
            return Result.Failure<AzureShareContainer>(ConfigurationErrors.Invalid(
                AzureStorageOptions.ConnectionStringVariable, "it is not an Azure Storage connection string."));
        }
    }

    // Azure container names: 3-63 characters, lowercase letters, digits and single hyphens, starting and ending with a letter or digit.
    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContainerName();
}
