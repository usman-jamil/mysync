using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Configuration;
using MySync.Domain.Publishing;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.Azure;

/// <summary>
/// Publications in the <c>$web</c> container of the publishing account's static website.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Server-side copies:</b> Put Blob From URL up to its limit (<see cref="AzureCopyLimits"/>);
/// above it, or where the service doesn't have it (Azurite answers 501), blocks staged from the
/// URL and committed with the headers. The bytes never pass through this machine.</item>
/// <item><b>Never configures the account:</b> static website hosting is checked, never turned on.</item>
/// <item><b>Secrets:</b> no error carries SDK text; the debug log gets it redacted. A rejected
/// credential is reported as the publishing account's, naming its variable.</item>
/// </list>
/// </remarks>
internal sealed class AzureStaticWebsitePublishTarget : IPublishTarget
{
    public const string WebContainer = "$web";

    private readonly ILogger _logger;
    private readonly AzureTransferSettings _settings;
    private readonly Lazy<Result<Connection>> _connection;
    private volatile bool _putBlobFromUrlMissing;

    public AzureStaticWebsitePublishTarget(IOptions<PublishStorageOptions> options, ILogger<AzureStaticWebsitePublishTarget>? logger = null)
        : this(options, AzureTransferSettings.Default, logger)
    {
    }

    internal AzureStaticWebsitePublishTarget(IOptions<PublishStorageOptions> options, AzureTransferSettings settings, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        PublishStorageOptions values = options.Value;
        _connection = new Lazy<Result<Connection>>(() => Connect(values, settings));
    }

    public Result<PublishLocation> GetLocation() =>
        _connection.Value.IsSuccess ? _connection.Value.Value.Location : Result.Failure<PublishLocation>(_connection.Value.Error);

    public async Task<Result> CheckStaticWebsiteAsync(CancellationToken cancellationToken)
    {
        if (_connection.Value.IsFailure)
        {
            return Result.Failure(_connection.Value.Error);
        }

        Connection connection = _connection.Value.Value;
        string account = connection.Location.AccountName;
        try
        {
            BlobServiceProperties properties = (await connection.Service.GetPropertiesAsync(cancellationToken)).Value;
            if (properties.StaticWebsite is not { Enabled: true } website)
            {
                return Result.Failure(PublishErrors.StaticWebsiteDisabled(account));
            }

            return string.Equals(website.IndexDocument, PublicationLayout.IndexDocument, StringComparison.Ordinal)
                ? Result.Success()
                : Result.Failure(PublishErrors.IndexDocumentMismatch(account, website.IndexDocument));
        }
        catch (RequestFailedException exception) when (exception.Status == 403)
        {
            Log(exception, "(service properties)");
            return Result.Failure(PublishErrors.CannotReadServiceProperties(account));
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure(Failed(exception, "(service properties)"));
        }
    }

    public Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken) =>
        CallAsync(blobName, async web =>
        {
            ArgumentNullException.ThrowIfNull(blob);
            try
            {
                await web.GetBlobClient(blobName).UploadAsync(
                    content,
                    new BlobUploadOptions
                    {
                        HttpHeaders = Headers(blob),
                        Conditions = onlyIfAbsent ? new BlobRequestConditions { IfNoneMatch = ETag.All } : null,
                        TransferOptions = new global::Azure.Storage.StorageTransferOptions { MaximumTransferSize = _settings.BlockSize, InitialTransferSize = _settings.BlockSize },
                    },
                    cancellationToken);
                return Result.Success(true);
            }
            catch (RequestFailedException exception) when (onlyIfAbsent && exception.Status is 409 or 412)
            {
                return Result.Success(false); // already there; left alone
            }
        }, cancellationToken);

    public Task<Result<CopyMethod>> CopyAsync(Uri source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken) =>
        CallAsync(blobName, async web =>
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(blob);
            BlockBlobClient target = web.GetBlockBlobClient(blobName);

            if (length <= AzureCopyLimits.PutBlobFromUrlMaxBytes && !_putBlobFromUrlMissing)
            {
                try
                {
                    await target.SyncUploadFromUriAsync(source, new BlobSyncUploadFromUriOptions { HttpHeaders = Headers(blob) }, cancellationToken);
                    return Result.Success(CopyMethod.PutBlobFromUrl);
                }
                catch (RequestFailedException exception) when (exception.Status == 501)
                {
                    _putBlobFromUrlMissing = true; // e.g. Azurite; blocks from the URL work there
                }
            }

            List<string> blocks = [];
            string uploadId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            for (long offset = 0; offset < length; offset += _settings.CopyBlockSize)
            {
                string blockId = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{uploadId}-{blocks.Count:D6}"));
                await target.StageBlockFromUriAsync(
                    source,
                    blockId,
                    new StageBlockFromUriOptions { SourceRange = new HttpRange(offset, Math.Min(_settings.CopyBlockSize, length - offset)) },
                    cancellationToken);
                blocks.Add(blockId);
            }

            await target.CommitBlockListAsync(blocks, new CommitBlockListOptions { HttpHeaders = Headers(blob) }, cancellationToken);
            return Result.Success(CopyMethod.BlocksFromUrl);
        }, cancellationToken);

    public async Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        Result<bool> deleted = await CallAsync(blobName, async web =>
        {
            await web.GetBlobClient(blobName).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
            return Result.Success(true);
        }, cancellationToken);

        return deleted.IsSuccess ? Result.Success() : Result.Failure(deleted.Error);
    }

    public Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken) =>
        CallAsync(prefix, async web =>
        {
            List<string> names = [];
            await foreach (BlobItem item in web.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
            {
                names.Add(item.Name);
            }

            await Parallel.ForEachAsync(
                names,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (name, token) => await web.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: token));
            return Result.Success(names.Count);
        }, cancellationToken);

    private static BlobHttpHeaders Headers(PublishedBlob blob) => new()
    {
        ContentType = blob.ContentType,
        ContentDisposition = blob.ContentDisposition,
        CacheControl = blob.CacheControl,
    };

    private static Result<Connection> Connect(PublishStorageOptions options, AzureTransferSettings settings)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return Result.Failure<Connection>(ConfigurationErrors.Missing(PublishStorageOptions.ConnectionStringVariable));
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Result.Failure<Connection>(ConfigurationErrors.Missing(PublishStorageOptions.BaseUrlVariable));
        }

        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out Uri? baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            return Result.Failure<Connection>(ConfigurationErrors.Invalid(
                PublishStorageOptions.BaseUrlVariable,
                "it must be the absolute https URL of the account's static website, e.g. https://<account>.z13.web.core.windows.net."));
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
            return new Connection(service, service.GetBlobContainerClient(WebContainer), new PublishLocation(account, baseUrl));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            // The SDK's message may quote the connection string, so it is not used.
            return Result.Failure<Connection>(ConfigurationErrors.Invalid(
                PublishStorageOptions.ConnectionStringVariable, "it is not an Azure Storage connection string."));
        }
    }

    private async Task<Result<T>> CallAsync<T>(string blobName, Func<BlobContainerClient, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        if (_connection.Value.IsFailure)
        {
            return Result.Failure<T>(_connection.Value.Error);
        }

        try
        {
            return await call(_connection.Value.Value.Web);
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "ContainerNotFound")
        {
            Log(exception, blobName);
            return Result.Failure<T>(PublishErrors.StaticWebsiteDisabled(_connection.Value.Value.Location.AccountName));
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<T>(Failed(exception, blobName));
        }
    }

    private Error Failed(Exception exception, string blobName)
    {
        Log(exception, blobName);
        Error error = AzureErrorMapper.Map(exception, blobName);
        return error.Code == StorageErrors.Unauthorized.Code ? PublishErrors.Unauthorized : error;
    }

    private void Log(Exception exception, string blobName) =>
        AzureLog.RequestFailed(_logger, blobName, AzureErrorMapper.StatusOf(exception), ConnectionStringRedactor.Redact(exception.Message));

    private sealed record Connection(BlobServiceClient Service, BlobContainerClient Web, PublishLocation Location);
}
