using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Configuration;
using MySync.Domain.Publishing;
using MySync.Domain.State;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>
/// Publications in a dedicated, public S3 bucket (CLAUDE.md §10a, "Publishing on S3"), at the keys
/// <see cref="PublicationLayout"/> builds: <c>p/&lt;publishId&gt;/…</c> and the root files.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Hosting:</b> the bucket's HTTPS REST endpoint (<c>https://&lt;bucket&gt;.s3.&lt;region&gt;.amazonaws.com</c>)
/// with a bucket policy allowing anonymous <c>s3:GetObject</c> on <c>p/*</c> and <c>robots.txt</c>
/// only, or CloudFront in front of it. The REST endpoint serves no index documents, so links end in
/// <c>index.html</c>; and for anyone who can't list the bucket a missing key is a 403, so 403 and 404
/// both mean "not served".</item>
/// <item><b>Preflight</b> proves it before anything is copied: the bucket is in its region, a probe is
/// readable anonymously through <c>MYSYNC_PUBLISH_BASE_URL</c>, and the bucket can't be listed
/// anonymously (a listing would reveal every publication ID).</item>
/// <item><b>Copies</b> stay inside S3 when the share is in S3 and signed with the same credentials:
/// CopyObject up to 5 GiB, a multipart copy above (S3 refuses CopyObject there). Anything else is
/// streamed through this machine by the caller.</item>
/// <item><b>Never configures the bucket:</b> the policy and Block Public Access are the owner's decision.</item>
/// </list>
/// </remarks>
internal sealed class S3PublishTarget : IPublishTarget, IDisposable
{
    internal const string ProbePrefix = "p/.mysync-probe-";

    private const char Slash = '/';

    private static readonly IReadOnlySet<int> NotServed = new HashSet<int> { 403, 404 };

    private readonly S3Connection _connection;
    private readonly Lazy<Result<PublishLocation>> _location;
    private readonly AnonymousProbe _probe;
    private readonly S3Uploader _uploader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly bool _ownsConnection;
    private string? _credentialScope;

    private readonly HttpClient? _ownHttp;

    public S3PublishTarget(IOptions<S3PublishOptions> options, TimeProvider timeProvider, ILogger<S3PublishTarget>? logger = null)
        : this(Connect(options, logger), options, http: null, timeProvider, logger, ownsConnection: true)
    {
    }

    /// <param name="http">The anonymous client the probe uses (tests: a stub server's); <c>null</c> for a plain one of its own.</param>
    internal S3PublishTarget(S3Connection connection, IOptions<S3PublishOptions> options, HttpClient? http, TimeProvider timeProvider, ILogger? logger, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownHttp = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
        _probe = new AnonymousProbe(http ?? _ownHttp!);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger.Instance;
        _uploader = new S3Uploader(connection.Settings, _timeProvider, _logger);
        _ownsConnection = ownsConnection;
        string? baseUrl = options.Value.BaseUrl;
        _location = new Lazy<Result<PublishLocation>>(() => Locate(_connection.Bucket, baseUrl));
    }

    public Result<PublishLocation> GetLocation() => _location.Value;

    public bool SharesAccountWith(StorageLocation shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return _connection.Bucket.IsSuccess
            && string.Equals(shares.Provider, S3ShareStore.ProviderName, StringComparison.Ordinal)
            && string.Equals(shares.Container, _connection.Bucket.Value.Name, StringComparison.Ordinal);
    }

    /// <summary>Only an S3 object readable with these same credentials: S3 can't copy from a URL.</summary>
    public bool CanCopy(CopySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Native is { Provider: S3ShareStore.ProviderName } native
            && _credentialScope is { } scope
            && string.Equals(native.CredentialScope, scope, StringComparison.Ordinal);
    }

    /// <summary>In order: the bucket and its region; a probe served anonymously; no anonymous listing.</summary>
    public async Task<Result> PreflightAsync(CancellationToken cancellationToken)
    {
        if (GetLocation() is { IsFailure: true } location)
        {
            return Result.Failure(location.Error);
        }

        Result<S3Client> client = await _connection.ClientAsync(cancellationToken);
        if (client.IsFailure)
        {
            return Result.Failure(Published(client.Error));
        }

        _credentialScope = client.Value.CredentialScope;
        S3Bucket bucket = _connection.Bucket.Value;
        Uri baseUrl = GetLocation().Value.BaseUrl;

        // 1. The bucket exists, in the configured region.
        Result inRegion = await RunAsync("(bucket)", S3Operation.List, async s3 =>
        {
            GetBucketLocationResponse found = await s3.Api.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket.Name }, cancellationToken);
            string actual = S3ShareStore.RegionOf(found.Location?.Value);
            return string.Equals(actual, bucket.Region, StringComparison.Ordinal)
                ? Result.Success(true)
                : Result.Failure<bool>(ConfigurationErrors.WrongRegion(bucket.Name, bucket.Region, actual));
        }, cancellationToken);
        if (inRegion.IsFailure)
        {
            return inRegion;
        }

        // 2. A probe, read back without credentials through the public address; removed whatever happens.
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string probeKey = ProbePrefix + token;
        Result written = await RunAsync(probeKey, S3Operation.Write, async s3 =>
        {
            Result<S3Written> put = await _uploader.PutAsync(
                s3, bucket, bucket.Key(probeKey), Encoding.UTF8.GetBytes(token), new S3Headers("text/plain; charset=utf-8", CacheControl: "no-store"), S3Condition.None, cancellationToken);
            return put.IsSuccess ? Result.Success(true) : Result.Failure<bool>(put.Error);
        }, cancellationToken);
        if (written.IsFailure)
        {
            return written;
        }

        bool served;
        try
        {
            served = await _probe.ServesAsync(new Uri(Root(baseUrl), probeKey), token, _timeProvider, cancellationToken);
        }
        finally
        {
            await RemoveProbeAsync(bucket, probeKey);
        }

        if (!served)
        {
            return Result.Failure(PublishErrors.NotPublic(bucket.Name, baseUrl.AbsoluteUri.TrimEnd('/')));
        }

        // 3. Nobody may list it: the publication IDs are the links' only secret.
        return await _probe.CanListAsync(baseUrl, cancellationToken)
            ? Result.Failure(PublishErrors.PublicListing(bucket.Name))
            : Result.Success();
    }

    public Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken) =>
        CallAsync(blobName, S3Operation.Write, async s3 =>
        {
            ArgumentNullException.ThrowIfNull(blob);
            S3Bucket bucket = _connection.Bucket.Value;
            Result<S3Written> written = await _uploader.UploadAsync(
                s3, bucket, bucket.Key(blobName), content, _ => Headers(blob), onlyIfAbsent ? S3Condition.IfAbsent : S3Condition.None,
                knownLength: content.CanSeek ? content.Length - content.Position : null, sizeHint: 0, beforeCommit: null, cancellationToken);
            if (written.IsFailure)
            {
                return Result.Failure<bool>(written.Error);
            }

            return written.Value.Outcome switch
            {
                S3WriteOutcome.Stored => Result.Success(true),
                S3WriteOutcome.ConditionFailed => Result.Success(false), // already there; left alone
                _ => Result.Failure<bool>(StorageErrors.Unreachable),
            };
        }, cancellationToken);

    public Task<Result<CopyMethod>> CopyAsync(CopySource source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blob);
        NativeObjectReference native = source?.Native is { } reference && CanCopy(source)
            ? reference
            : throw new ArgumentException("S3 copies only from an S3 object it can read with its own credentials (see CanCopy).", nameof(source));

        return CallAsync(blobName, S3Operation.Write, async s3 =>
        {
            IAmazonS3 api = s3.Api;
            S3Bucket bucket = _connection.Bucket.Value;
            string key = bucket.Key(blobName);
            S3Headers headers = Headers(blob);

            if (length <= _connection.Settings.CopyObjectMaxBytes)
            {
                // Publication IDs are unique, so the destination is new: no condition needed.
                CopyObjectRequest copy = new()
                {
                    SourceBucket = native.Container,
                    SourceKey = native.Key,
                    DestinationBucket = bucket.Name,
                    DestinationKey = key,
                    MetadataDirective = S3MetadataDirective.REPLACE,
                    ContentType = headers.ContentType,
                };
                S3Uploader.Apply(copy.Headers, copy.Metadata, headers);
                await api.CopyObjectAsync(copy, cancellationToken);
                return Result.Success(CopyMethod.CopyObject);
            }

            return await MultipartCopyAsync(api, native, bucket, key, headers, length, cancellationToken);
        }, cancellationToken);
    }

    public async Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        Result<bool> deleted = await CallAsync(blobName, S3Operation.Delete, async s3 =>
        {
            S3Bucket bucket = _connection.Bucket.Value;
            await s3.Api.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket.Name, Key = bucket.Key(blobName) }, cancellationToken);
            return Result.Success(true);
        }, cancellationToken);

        return deleted.IsSuccess ? Result.Success() : Result.Failure(deleted.Error);
    }

    /// <summary>
    /// Every key under the prefix, in DeleteObjects batches of up to 1,000, each entry of each answer
    /// checked (S3 reports per-key failures inside a 200). A publication's <c>index.html</c> goes first,
    /// on its own, so its link is dead before anything else is removed.
    /// </summary>
    public Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken) =>
        CallAsync(prefix, S3Operation.Delete, async s3 =>
        {
            IAmazonS3 api = s3.Api;
            S3Bucket bucket = _connection.Bucket.Value;
            List<string> keys = [];
            await foreach (Result<S3Object> item in S3Listing.ListAsync(_connection, prefix, pageSize: null, _logger, cancellationToken))
            {
                if (item.IsFailure)
                {
                    return Result.Failure<int>(item.Error);
                }

                keys.Add(item.Value.Key);
            }

            string index = bucket.Key(prefix + PublicationLayout.IndexDocument);
            int deleted = 0;
            if (keys.Remove(index))
            {
                await api.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket.Name, Key = index }, cancellationToken);
                deleted++;
            }

            foreach (string[] batch in keys.Chunk(1000))
            {
                DeleteObjectsResponse response = await api.DeleteObjectsAsync(
                    new DeleteObjectsRequest { BucketName = bucket.Name, Objects = [.. batch.Select(key => new KeyVersion { Key = key })], Quiet = true },
                    cancellationToken);
                if (response.DeleteErrors is { Count: > 0 } errors)
                {
                    DeleteError first = errors[0];
                    S3Log.RequestFailed(_logger, first.Key, 200, first.Code ?? "DeleteError", S3Redactor.Redact(first.Message));
                    return Result.Failure<int>(first.Code is "AccessDenied"
                        ? PublishErrors.UnauthorizedBecause(S3ErrorMapper.PermissionsHint)
                        : StorageErrors.Unreachable);
                }
            }

            return Result.Success(deleted + keys.Count);
        }, cancellationToken);

    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }

        _ownHttp?.Dispose();
    }

    internal static Result<PublishLocation> Locate(Result<S3Bucket> bucket, string? baseUrlSetting)
    {
        if (bucket.IsFailure)
        {
            return Result.Failure<PublishLocation>(bucket.Error);
        }

        if (string.IsNullOrWhiteSpace(baseUrlSetting))
        {
            return Result.Failure<PublishLocation>(ConfigurationErrors.Missing(S3PublishOptions.BaseUrlVariable));
        }

        if (!Uri.TryCreate(baseUrlSetting.Trim(), UriKind.Absolute, out Uri? baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            return Result.Failure<PublishLocation>(ConfigurationErrors.Invalid(
                S3PublishOptions.BaseUrlVariable,
                $"it must be the bucket's absolute https address, e.g. https://{bucket.Value.Name}.s3.{bucket.Value.Region}.amazonaws.com, or a CloudFront distribution in front of it. " +
                "(The S3 website endpoint is http only, so it can't be used.)"));
        }

        return new PublishLocation(S3ShareStore.ProviderName, "s3://" + bucket.Value.Name, baseUrl, ServesIndexDocuments: false, NotServed);
    }

    private static S3Connection Connect(IOptions<S3PublishOptions> options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        S3PublishOptions values = options.Value;
        string? profile = string.IsNullOrWhiteSpace(values.Profile) ? null : values.Profile.Trim();
        return new S3Connection(
            () => S3Bucket.From(values.Bucket, values.Region, keyPrefix: string.Empty, S3PublishOptions.BucketVariable, $"{S3PublishOptions.RegionVariable} (or MYSYNC_S3_REGION, AWS_REGION)"),
            profile,
            S3TransferSettings.Default,
            logger);
    }

    private static S3Headers Headers(PublishedBlob blob) => new(blob.ContentType, blob.ContentEncoding, blob.ContentDisposition, blob.CacheControl);

    private static Uri Root(Uri baseUrl) => baseUrl.AbsoluteUri.EndsWith(Slash) ? baseUrl : new Uri(baseUrl.AbsoluteUri + Slash);

    private async Task<Result<CopyMethod>> MultipartCopyAsync(
        IAmazonS3 api, NativeObjectReference source, S3Bucket bucket, string key, S3Headers headers, long length, CancellationToken cancellationToken)
    {
        InitiateMultipartUploadRequest start = new() { BucketName = bucket.Name, Key = key, ContentType = headers.ContentType };
        S3Uploader.Apply(start.Headers, start.Metadata, headers);
        string uploadId = (await api.InitiateMultipartUploadAsync(start, cancellationToken)).UploadId;
        try
        {
            long partSize = _connection.Settings.CopyPartSize ?? S3PartSizes.ForCopy(length);
            List<PartETag> parts = [];
            int number = 1;
            for (long offset = 0; offset < length; offset += partSize, number++)
            {
                CopyPartResponse copied = await api.CopyPartAsync(
                    new CopyPartRequest
                    {
                        SourceBucket = source.Container,
                        SourceKey = source.Key,
                        DestinationBucket = bucket.Name,
                        DestinationKey = key,
                        UploadId = uploadId,
                        PartNumber = number,
                        FirstByte = offset,
                        LastByte = Math.Min(offset + partSize, length) - 1,
                    },
                    cancellationToken);
                parts.Add(new PartETag(number, copied.ETag));
            }

            await api.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket.Name, Key = key, UploadId = uploadId, PartETags = parts }, cancellationToken);
            return CopyMethod.MultipartCopy;
        }
        catch (Exception)
        {
            try
            {
                await api.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket.Name, Key = key, UploadId = uploadId }, CancellationToken.None);
            }
            catch (Exception exception) when (S3ErrorMapper.IsStorageFailure(exception, CancellationToken.None))
            {
                S3Log.AbortFailed(_logger, key, S3Redactor.Redact(exception.Message));
            }

            throw;
        }
    }

    private async Task RemoveProbeAsync(S3Bucket bucket, string probeKey)
    {
        Result removed = await DeleteAsync(probeKey, CancellationToken.None);
        if (removed.IsFailure)
        {
            S3Log.ProbeNotRemoved(_logger, bucket.Key(probeKey), removed.Error.Code);
        }
    }

    private async Task<Result<T>> CallAsync<T>(string name, S3Operation operation, Func<S3Client, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        if (GetLocation() is { IsFailure: true } location)
        {
            return Result.Failure<T>(location.Error);
        }

        Result<S3Client> client = await _connection.ClientAsync(cancellationToken);
        if (client.IsFailure)
        {
            return Result.Failure<T>(Published(client.Error));
        }

        try
        {
            return await call(client.Value);
        }
        catch (Exception exception) when (S3ErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<T>(Published(S3Calls.Failed(_logger, exception, name, operation, _connection.Bucket.Value.Target)));
        }
    }

    private async Task<Result> RunAsync(string name, S3Operation operation, Func<S3Client, Task<Result<bool>>> call, CancellationToken cancellationToken)
    {
        Result<bool> outcome = await CallAsync(name, operation, call, cancellationToken);
        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);
    }

    // A rejected credential is the publishing storage's, so it says which.
    private static Error Published(Error error) =>
        error.Code == "Storage.Unauthorized"
            ? PublishErrors.UnauthorizedBecause(error.Description.Replace("The storage rejected the request. ", string.Empty, StringComparison.Ordinal))
            : error;
}
