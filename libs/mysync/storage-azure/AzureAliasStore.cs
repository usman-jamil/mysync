using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.Azure;

/// <summary>
/// Share names in the share container: <c>aliases/&lt;name&gt;</c> (<see cref="ShareLayout.Alias"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each alias is one block blob, <c>application/json</c>, written in one Put with <c>If-None-Match: *</c>:
/// a JSON body (<c>shareId</c>, <c>createdUtc</c>, <c>createdBy</c>) for people and other providers,
/// and the same three values as metadata, which is what this store reads.</item>
/// <item>Listing is one paged listing of <c>aliases/</c> with metadata (up to 5,000 per page), never a read per alias.</item>
/// <item>Same container, configuration, error mapping and redaction as <see cref="AzureBlobShareStore"/>.</item>
/// </list>
/// </remarks>
internal sealed class AzureAliasStore : IAliasStore
{
    internal const string ShareIdKey = "mysyncshareid";
    internal const string CreatedUtcKey = "mysynccreatedutc";
    internal const string CreatedByKey = "mysynccreatedby";

    private const string ContentType = "application/json";
    private const string TimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'";

    private readonly ILogger _logger;
    private readonly int? _pageSize;
    private readonly Lazy<Result<AzureShareContainer>> _container;

    public AzureAliasStore(IOptions<AzureStorageOptions> options, ILogger<AzureAliasStore>? logger = null)
        : this(options, AzureTransferSettings.Default, pageSize: null, logger)
    {
    }

    /// <param name="pageSize">Aliases per listing page; tests make it small to cross pages. Azure's default (5,000) otherwise.</param>
    internal AzureAliasStore(IOptions<AzureStorageOptions> options, AzureTransferSettings settings, int? pageSize, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger.Instance;
        _pageSize = pageSize;
        AzureStorageOptions values = options.Value;
        _container = new Lazy<Result<AzureShareContainer>>(() => AzureShareContainer.Connect(values, settings));
    }

    public Task<Result<string>> CreateAsync(AliasRecord alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alias);
        string name = ShareLayout.Alias(alias.Name);

        return CallAsync(name, async container =>
        {
            AliasBodyJson body = new(alias.ShareId.ToString(), alias.CreatedUtc, alias.CreatedBy);
            try
            {
                Response<BlobContentInfo> written = await container.GetBlobClient(name).UploadAsync(
                    BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(body, AliasJsonContext.Default.AliasBodyJson)),
                    new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = new BlobHttpHeaders { ContentType = ContentType },
                        Metadata = new Dictionary<string, string>
                        {
                            [ShareIdKey] = alias.ShareId.ToString(),
                            [CreatedUtcKey] = alias.CreatedUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                            [CreatedByKey] = Uri.EscapeDataString(alias.CreatedBy), // metadata must be ASCII
                        },
                    },
                    cancellationToken);
                return Result.Success(written.Value.ETag.ToString());
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                return Result.Failure<string>(StorageErrors.AlreadyExists(name));
            }
        }, cancellationToken);
    }

    public Task<Result<AliasSnapshot?>> GetAsync(ShareName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        string blobName = ShareLayout.Alias(name);

        return CallAsync<AliasSnapshot?>(blobName, async container =>
        {
            BlobProperties properties;
            try
            {
                properties = (await container.GetBlobClient(blobName).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return Result.Success<AliasSnapshot?>(null); // no such name, or no container yet
            }

            Result<AliasRecord> alias = FromMetadata(name, properties.Metadata);
            return alias.IsSuccess
                ? Result.Success<AliasSnapshot?>(new AliasSnapshot(alias.Value, properties.ETag.ToString()))
                : Result.Failure<AliasSnapshot?>(alias.Error);
        }, cancellationToken);
    }

    public async IAsyncEnumerable<Result<AliasRecord>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_container.Value.IsFailure)
        {
            yield return Result.Failure<AliasRecord>(_container.Value.Error);
            yield break;
        }

        await using IAsyncEnumerator<Page<BlobItem>> pages = _container.Value.Value.Container
            .GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, ShareLayout.AliasesPrefix, cancellationToken)
            .AsPages(pageSizeHint: _pageSize)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            Result<bool> next = await MoveNextAsync(pages, cancellationToken);
            if (next.IsFailure)
            {
                if (next.Error.Type != ErrorType.NotFound)
                {
                    yield return Result.Failure<AliasRecord>(next.Error); // no container: no names
                }

                yield break;
            }

            if (!next.Value)
            {
                yield break;
            }

            foreach (BlobItem item in pages.Current.Values)
            {
                yield return ShareLayout.TryParseAlias(item.Name, out ShareName? name)
                    ? FromMetadata(name, item.Metadata)
                    : Result.Failure<AliasRecord>(StorageErrors.StrayAlias(item.Name));
            }
        }
    }

    public async Task<Result<IReadOnlyList<AliasRecord>>> FindByShareAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        List<AliasRecord> found = [];
        await foreach (Result<AliasRecord> alias in ListAsync(cancellationToken))
        {
            if (alias.IsSuccess)
            {
                if (alias.Value.ShareId == shareId)
                {
                    found.Add(alias.Value);
                }
            }
            else if (alias.Error.Code is not ("Storage.InvalidAlias" or "Storage.StrayAlias"))
            {
                return Result.Failure<IReadOnlyList<AliasRecord>>(alias.Error);
            }
        }

        return found;
    }

    public async Task<Result> DeleteAsync(ShareName name, string ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        string blobName = ShareLayout.Alias(name);

        Result<bool> deleted = await CallAsync(blobName, async container =>
        {
            try
            {
                await container.GetBlobClient(blobName).DeleteAsync(
                    DeleteSnapshotsOption.IncludeSnapshots,
                    new BlobRequestConditions { IfMatch = new ETag(ifMatchETag) },
                    cancellationToken);
                return Result.Success(true);
            }
            catch (RequestFailedException exception) when (exception.Status is 412 or 404)
            {
                // Changed (or removed) since it was read: it may point at another share now.
                return Result.Failure<bool>(StorageErrors.PreconditionFailed(blobName));
            }
        }, cancellationToken);

        return deleted.IsSuccess ? Result.Success() : Result.Failure(deleted.Error);
    }

    private static Result<AliasRecord> FromMetadata(ShareName name, IDictionary<string, string>? metadata)
    {
        string? Value(string key) => metadata is not null && metadata.TryGetValue(key, out string? value) ? value : null;

        if (!ShareId.TryParse(Value(ShareIdKey), out ShareId? shareId))
        {
            return Result.Failure<AliasRecord>(StorageErrors.InvalidAlias(name.Value, "its share ID is missing or malformed"));
        }

        DateTimeOffset createdUtc = DateTimeOffset.TryParse(Value(CreatedUtcKey), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        string createdBy;
        try
        {
            createdBy = Uri.UnescapeDataString(Value(CreatedByKey) ?? string.Empty);
        }
        catch (UriFormatException)
        {
            createdBy = string.Empty;
        }

        return new AliasRecord(name, shareId, createdUtc, createdBy);
    }

    private async Task<Result<bool>> MoveNextAsync(IAsyncEnumerator<Page<BlobItem>> pages, CancellationToken cancellationToken)
    {
        try
        {
            return await pages.MoveNextAsync();
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<bool>(Failed(exception, ShareLayout.AliasesPrefix));
        }
    }

    private async Task<Result<T>> CallAsync<T>(string blobName, Func<BlobContainerClient, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        if (_container.Value.IsFailure)
        {
            return Result.Failure<T>(_container.Value.Error);
        }

        try
        {
            return await call(_container.Value.Value.Container);
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<T>(Failed(exception, blobName));
        }
    }

    private Error Failed(Exception exception, string blobName)
    {
        AzureLog.RequestFailed(_logger, blobName, AzureErrorMapper.StatusOf(exception), ConnectionStringRedactor.Redact(exception.Message));
        return AzureErrorMapper.Map(exception, blobName);
    }
}

/// <summary>The body of <c>aliases/&lt;name&gt;</c>: for people reading the container, and for other providers.</summary>
internal sealed record AliasBodyJson(string ShareId, DateTimeOffset CreatedUtc, string CreatedBy);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AliasBodyJson))]
internal sealed partial class AliasJsonContext : JsonSerializerContext;
