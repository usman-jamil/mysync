using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>
/// Share names in the share bucket: <c>aliases/&lt;name&gt;</c> (<see cref="ShareLayout.Alias"/>),
/// the same objects as on Azure (CLAUDE.md §6a).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The body is the source of truth</b>, <c>{ "shareId", "createdUtc", "createdBy" }</c>; the same
/// three values are written as <c>x-amz-meta-*</c> in the same PutObject (URL-encoded where needed:
/// metadata is ASCII), a copy for listing. S3's ETag follows the body, and the body holds the share ID,
/// so an alias re-pointed at another share always has another ETag: a conditional delete that read the
/// old one can't remove it. An alias deleted and created again for the <b>same</b> share, by the same
/// person at the same instant, has the same bytes and keeps its ETag; a stale delete then removes an
/// alias that says exactly what the caller read, which is accepted.</item>
/// <item><see cref="GetAsync"/> reads the body. S3 listings don't return metadata, so
/// <see cref="ListAsync"/> is one listing of <c>aliases/</c> plus a HEAD per alias, at most
/// <c>MYSYNC_PARALLELISM</c> at once.</item>
/// </list>
/// </remarks>
internal sealed class S3AliasStore : IAliasStore
{
    internal const string ShareIdKey = "mysyncshareid";
    internal const string CreatedUtcKey = "mysynccreatedutc";
    internal const string CreatedByKey = "mysynccreatedby";

    private const string ContentType = "application/json";
    private const string TimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'";

    private readonly S3Connection _connection;
    private readonly S3Uploader _uploader;
    private readonly ILogger _logger;
    private readonly int _parallelism;
    private readonly int? _pageSize;

    public S3AliasStore(S3ShareBucket bucket, TimeProvider timeProvider, SyncOptions? options = null, ILogger<S3AliasStore>? logger = null)
        : this(bucket?.Connection!, timeProvider, options, pageSize: null, logger)
    {
    }

    /// <param name="pageSize">Aliases per listing page; tests make it small to cross pages.</param>
    internal S3AliasStore(S3Connection connection, TimeProvider timeProvider, SyncOptions? options, int? pageSize, ILogger? logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger.Instance;
        _uploader = new S3Uploader(connection.Settings, timeProvider ?? TimeProvider.System, _logger);
        _parallelism = (options ?? new SyncOptions()).MaxParallelism;
        _pageSize = pageSize;
    }

    public Task<Result<string>> CreateAsync(AliasRecord alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alias);
        string name = ShareLayout.Alias(alias.Name);

        return CallAsync(name, S3Operation.Write, async (client, bucket) =>
        {
            AliasBodyJson body = new(alias.ShareId.ToString(), alias.CreatedUtc, alias.CreatedBy);
            S3Headers headers = new(ContentType, Metadata: new Dictionary<string, string>
            {
                [ShareIdKey] = alias.ShareId.ToString(),
                [CreatedUtcKey] = alias.CreatedUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                [CreatedByKey] = Uri.EscapeDataString(alias.CreatedBy), // metadata must be ASCII
            });

            Result<S3Written> written = await _uploader.PutAsync(
                client, bucket, bucket.Key(name), JsonSerializer.SerializeToUtf8Bytes(body, S3AliasJsonContext.Default.AliasBodyJson), headers, S3Condition.IfAbsent, cancellationToken);
            if (written.IsFailure)
            {
                return Result.Failure<string>(written.Error);
            }

            return written.Value.Outcome == S3WriteOutcome.Stored
                ? Result.Success(written.Value.ETag ?? string.Empty)
                : Result.Failure<string>(StorageErrors.AlreadyExists(name));
        }, cancellationToken);
    }

    public Task<Result<AliasSnapshot?>> GetAsync(ShareName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        string blobName = ShareLayout.Alias(name);

        return CallAsync<AliasSnapshot?>(blobName, S3Operation.Read, async (client, bucket) =>
        {
            (byte[] Bytes, string ETag)? read = await S3ShareStore.GetBytesAsync(client, bucket, blobName, cancellationToken);
            if (read is not { } found)
            {
                return Result.Success<AliasSnapshot?>(null);
            }

            Result<AliasRecord> alias = FromBody(name, found.Bytes);
            return alias.IsSuccess
                ? Result.Success<AliasSnapshot?>(new AliasSnapshot(alias.Value, found.ETag))
                : Result.Failure<AliasSnapshot?>(alias.Error);
        }, cancellationToken);
    }

    public async IAsyncEnumerable<Result<AliasRecord>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<S3Object> page = [];
        await foreach (Result<S3Object> item in S3Listing.ListAsync(_connection, ShareLayout.AliasesPrefix, _pageSize, _logger, cancellationToken))
        {
            if (item.IsFailure)
            {
                yield return Result.Failure<AliasRecord>(item.Error);
                yield break;
            }

            page.Add(item.Value);
            if (page.Count >= (_pageSize ?? 1000))
            {
                foreach (Result<AliasRecord> alias in await DescribeAsync(page, cancellationToken))
                {
                    yield return alias;
                }

                page.Clear();
            }
        }

        foreach (Result<AliasRecord> alias in await DescribeAsync(page, cancellationToken))
        {
            yield return alias;
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

    /// <summary>DeleteObject with <c>If-Match</c>: a 412 (changed) or a 404 (already gone) is <c>PreconditionFailed</c>.</summary>
    public async Task<Result> DeleteAsync(ShareName name, string ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        string blobName = ShareLayout.Alias(name);

        Result<bool> deleted = await CallAsync(blobName, S3Operation.ConditionalDelete, async (client, bucket) =>
        {
            try
            {
                await client.Api.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket.Name, Key = bucket.Key(blobName), IfMatch = ifMatchETag }, cancellationToken);
                return Result.Success(true);
            }
            catch (AmazonS3Exception exception) when (S3ErrorMapper.IsPreconditionFailed(exception) || S3ErrorMapper.IsMissingKey(exception))
            {
                // Changed (or removed) since it was read: it may point at another share now.
                return Result.Failure<bool>(StorageErrors.PreconditionFailed(blobName));
            }
        }, cancellationToken);

        return deleted.IsSuccess ? Result.Success() : Result.Failure(deleted.Error);
    }

    internal static Result<AliasRecord> FromBody(ShareName name, byte[] body)
    {
        AliasBodyJson? json;
        try
        {
            json = JsonSerializer.Deserialize(body, S3AliasJsonContext.Default.AliasBodyJson);
        }
        catch (JsonException)
        {
            json = null;
        }

        return json is not null && ShareId.TryParse(json.ShareId, out ShareId? shareId)
            ? new AliasRecord(name, shareId, json.CreatedUtc, json.CreatedBy ?? string.Empty)
            : Result.Failure<AliasRecord>(StorageErrors.InvalidAlias(name.Value, "its share ID is missing or malformed"));
    }

    internal static Result<AliasRecord> FromMetadata(ShareName name, MetadataCollection metadata)
    {
        string? Value(string key)
        {
            if (metadata.Keys.Contains("x-amz-meta-" + key))
            {
                return metadata["x-amz-meta-" + key];
            }

            return metadata.Keys.Contains(key) ? metadata[key] : null;
        }

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

    // One HEAD per alias, bounded; results in listing order. An alias deleted since the listing is left out.
    private async Task<List<Result<AliasRecord>>> DescribeAsync(List<S3Object> page, CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return [];
        }

        var described = new Result<AliasRecord>?[page.Count];
        Result<bool> done = await CallAsync(ShareLayout.AliasesPrefix, S3Operation.Read, async (client, bucket) =>
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, page.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    string? layoutName = bucket.LayoutName(page[index].Key);
                    if (layoutName is null || !ShareLayout.TryParseAlias(layoutName, out ShareName? name))
                    {
                        described[index] = Result.Failure<AliasRecord>(StorageErrors.StrayAlias(layoutName ?? page[index].Key));
                        return;
                    }

                    GetObjectMetadataResponse? head = await S3ShareStore.HeadAsync(client, bucket.Name, page[index].Key, token);
                    if (head is not null)
                    {
                        described[index] = FromMetadata(name, head.Metadata);
                    }
                });
            return Result.Success(true);
        }, cancellationToken);

        return done.IsFailure ? [Result.Failure<AliasRecord>(done.Error)] : [.. described.OfType<Result<AliasRecord>>()];
    }

    private async Task<Result<T>> CallAsync<T>(string name, S3Operation operation, Func<S3Client, S3Bucket, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        Result<S3Client> client = await _connection.ClientAsync(cancellationToken);
        if (client.IsFailure)
        {
            return Result.Failure<T>(client.Error);
        }

        S3Bucket bucket = _connection.Bucket.Value;
        try
        {
            return await call(client.Value, bucket);
        }
        catch (Exception exception) when (S3ErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<T>(S3Calls.Failed(_logger, exception, name, operation, bucket.Target));
        }
    }
}

/// <summary>The body of <c>aliases/&lt;name&gt;</c>, byte-for-byte the shape Azure writes.</summary>
internal sealed record AliasBodyJson(string ShareId, DateTimeOffset CreatedUtc, string? CreatedBy);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AliasBodyJson))]
internal sealed partial class S3AliasJsonContext : JsonSerializerContext;
