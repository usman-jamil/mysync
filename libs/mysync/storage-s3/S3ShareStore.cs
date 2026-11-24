using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>
/// A share in an S3 general purpose bucket, laid out by <see cref="ShareLayout"/> exactly as in an
/// Azure container (CLAUDE.md §6, §11): <c>shares/&lt;id&gt;/manifest.json</c>, <c>manifests/</c>,
/// <c>objects/</c>, <c>publications.json</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Commit point:</b> <c>manifest.json</c> is a PutObject with <c>If-None-Match: *</c> (new
/// share) or <c>If-Match</c> (existing share). S3's ETag follows the bytes, not the write; that is
/// sound here because no two commits ever write the same manifest bytes (every commit has a new
/// revision). The same holds for <c>publications.json</c>: every change alters its records.</item>
/// <item><b>Objects are verified before they exist:</b> the bytes are hashed while they're read, and
/// the PutObject (or the multipart completion) only happens if the hash is the object's name.
/// <see cref="S3Uploader"/> holds at most one part in memory.</item>
/// <item><b>A listing doesn't carry an object's <c>Content-Encoding</c></b>, so
/// <see cref="ListObjectsAsync"/> reads each object's headers (HEAD, at most
/// <c>MYSYNC_PARALLELISM</c> at once).</item>
/// <item><b>No bucket is ever created:</b> the bucket is the team's (no <c>s3:CreateBucket</c> in the
/// policy); "ready for a new share" checks it exists in the configured region.</item>
/// <item><b>Secrets:</b> no error carries SDK text; the debug log gets it redacted.</item>
/// </list>
/// </remarks>
internal sealed class S3ShareStore : IShareStore
{
    public const string ProviderName = "s3";

    internal const string ManifestContentType = "application/json";
    internal const string ObjectContentType = "application/octet-stream";

    /// <summary>A presigned URL never outlives this (and never the credentials that signed it).</summary>
    private static readonly TimeSpan MaxPresignedLifetime = TimeSpan.FromHours(1);

    private readonly S3Connection _connection;
    private readonly IManifestSerializer _serializer;
    private readonly IPublicationRegistrySerializer _registrySerializer;
    private readonly TimeProvider _timeProvider;
    private readonly S3Uploader _uploader;
    private readonly int _parallelism;
    private readonly ILogger _logger;

    public S3ShareStore(
        S3ShareBucket bucket,
        IManifestSerializer serializer,
        IPublicationRegistrySerializer registrySerializer,
        TimeProvider timeProvider,
        SyncOptions? options = null,
        ILogger<S3ShareStore>? logger = null)
        : this(bucket?.Connection!, serializer, registrySerializer, timeProvider, options, (ILogger?)logger)
    {
    }

    internal S3ShareStore(
        S3Connection connection,
        IManifestSerializer serializer,
        IPublicationRegistrySerializer registrySerializer,
        TimeProvider timeProvider,
        SyncOptions? options,
        ILogger? logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registrySerializer = registrySerializer ?? throw new ArgumentNullException(nameof(registrySerializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger.Instance;
        _parallelism = (options ?? new SyncOptions()).MaxParallelism;
        _uploader = new S3Uploader(connection.Settings, timeProvider, _logger);
    }

    public Result<StorageLocation> GetLocation() =>
        _connection.Bucket.IsSuccess
            ? new StorageLocation(ProviderName, _connection.Bucket.Value.Region, _connection.Bucket.Value.Name)
            : Result.Failure<StorageLocation>(_connection.Bucket.Error);

    /// <summary>The bucket exists, in the configured region. Never creates one.</summary>
    public Task<Result> EnsureReadyForNewShareAsync(CancellationToken cancellationToken) =>
        CallAsync("(bucket)", S3Operation.List, async (client, bucket) =>
        {
            await S3Listing.CheckBucketAsync(client, bucket, cancellationToken);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<ManifestSnapshot?>> GetManifestAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Manifest;

        return CallAsync<ManifestSnapshot?>(name, S3Operation.Read, async (client, bucket) =>
        {
            (byte[] Bytes, string ETag)? read = await GetBytesAsync(client, bucket, name, cancellationToken);
            if (read is not { } found)
            {
                return Result.Success<ManifestSnapshot?>(null);
            }

            Result<Manifest> manifest = _serializer.Deserialize(found.Bytes);
            return manifest.IsSuccess
                ? Result.Success<ManifestSnapshot?>(new ManifestSnapshot(manifest.Value, found.ETag))
                : Result.Failure<ManifestSnapshot?>(manifest.Error);
        }, cancellationToken);
    }

    public Task<Result<string>> CreateManifestAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken) =>
        PutJsonAsync(ShareLayout.For(shareId).Manifest, _serializer.Serialize(manifest), S3Condition.IfAbsent, name => StorageErrors.AlreadyExists(name), cancellationToken);

    /// <summary>A 412, or a 404 (HEAD is gone), is <c>PreconditionFailed</c>: either way the pipeline re-reads.</summary>
    public Task<Result<string>> ReplaceManifestAsync(ShareId shareId, Manifest manifest, string ifMatchETag, CancellationToken cancellationToken) =>
        PutJsonAsync(ShareLayout.For(shareId).Manifest, _serializer.Serialize(manifest), S3Condition.IfMatch(ifMatchETag), name => StorageErrors.PreconditionFailed(name), cancellationToken);

    public async Task<Result> PutManifestHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string name = ShareLayout.For(shareId).ManifestHistory(manifest.Revision);

        // History is immutable: a revision already stored is a success.
        Result<string> written = await PutJsonAsync(name, _serializer.Serialize(manifest), S3Condition.IfAbsent, _ => null, cancellationToken);
        return written.IsSuccess ? Result.Success() : Result.Failure(written.Error);
    }

    public async IAsyncEnumerable<Result<Manifest>> ListManifestHistoryAsync(ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = ShareLayout.For(shareId);
        List<(Revision Revision, string Name)> found = [];

        await foreach (Result<S3Object> item in ListAsync(layout.ManifestHistoryPrefix, cancellationToken))
        {
            if (item.IsFailure)
            {
                yield return Result.Failure<Manifest>(item.Error);
                yield break;
            }

            if (Name(item.Value) is { } name && layout.TryParseManifestHistory(name, out Revision? revision))
            {
                found.Add((revision, name));
            }
        }

        found.Sort((left, right) => left.Revision.CompareTo(right.Revision));

        foreach ((_, string name) in found)
        {
            yield return await CallAsync(name, S3Operation.Read, async (client, bucket) =>
            {
                (byte[] Bytes, string ETag)? read = await GetBytesAsync(client, bucket, name, cancellationToken);
                return read is { } history ? _serializer.Deserialize(history.Bytes) : Result.Failure<Manifest>(StorageErrors.NotFound(name));
            }, cancellationToken);
        }
    }

    public Task<Result> DeleteManifestHistoryAsync(ShareId shareId, Revision revision, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).ManifestHistory(revision), cancellationToken);

    public Task<Result<bool>> ObjectExistsAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync(name, S3Operation.Read, async (client, bucket) =>
            Result.Success(await HeadAsync(client, bucket.Name, bucket.Key(name), cancellationToken) is not null), cancellationToken);
    }

    /// <remarks>
    /// The bytes are hashed while <see cref="S3Uploader"/> reads them; only after the last byte the
    /// producer's verdict, then for raw bytes our own SHA-256 and length check, then the PutObject or
    /// completion with <c>If-None-Match: *</c> (412: another upload of the same object won, with the
    /// same bytes). A multipart completion that S3 answers 409 is restarted from scratch when the
    /// content can be read again, after checking whether the competing upload stored the object.
    /// </remarks>
    public Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, ObjectUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(upload);
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        // Reading the content may throw IOException (the local file); that is the caller's to handle.
        return CallAsync(name, S3Operation.Write, async (client, bucket) =>
        {
            string key = bucket.Key(name);
            if (await HeadAsync(client, bucket.Name, key, cancellationToken) is not null)
            {
                return Result.Success(); // same name, same bytes
            }

            long start = upload.Content.CanSeek ? upload.Content.Position : -1;
            int attempt = 0;
            while (true)
            {
                attempt++;
                Result<S3Written> written = await UploadOnceAsync(client, bucket, name, key, hash, upload, cancellationToken);
                if (written.IsFailure)
                {
                    return Result.Failure(written.Error);
                }

                if (written.Value.Outcome != S3WriteOutcome.Conflict)
                {
                    return Result.Success(); // stored, or already stored by someone else
                }

                // 409 on completion: someone else was writing this key. Did they store it?
                await Task.Delay(_uploader.Backoff(attempt), _timeProvider, cancellationToken);
                if (await HeadAsync(client, bucket.Name, key, cancellationToken) is not null)
                {
                    return Result.Success();
                }

                if (start < 0 || attempt >= _connection.Settings.ConditionalConflictAttempts)
                {
                    return Result.Failure(StorageErrors.Unreachable); // can't read the content again: the next run uploads it
                }

                upload.Content.Position = start;
            }
        }, cancellationToken);
    }

    /// <remarks>
    /// The stored bytes: the response stream isn't decoded by the SDK or its HTTP handler (pinned by
    /// the contract case <c>AGzipObject_Should_ReadBackAsStored_NotDecoded</c> on real S3).
    /// </remarks>
    public Task<Result<StoredObject>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync(name, S3Operation.Read, async (client, bucket) =>
        {
            GetObjectResponse response;
            try
            {
                response = await client.Api.GetObjectAsync(new GetObjectRequest { BucketName = bucket.Name, Key = bucket.Key(name) }, cancellationToken);
            }
            catch (AmazonS3Exception exception) when (S3ErrorMapper.IsMissingKey(exception))
            {
                return Result.Failure<StoredObject>(StorageErrors.NotFound(name));
            }

            Result<ObjectEncoding> encoding = EncodingOf(response.Headers.ContentEncoding);
            if (encoding.IsFailure)
            {
                response.Dispose();
                return Result.Failure<StoredObject>(encoding.Error);
            }

            return Result.Success(new StoredObject(new S3ReadStream(response, name, _logger), encoding.Value, response.ContentLength));
        }, cancellationToken);
    }

    public async IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = ShareLayout.For(shareId);
        List<(ContentHash Hash, S3Object Item)> page = [];

        await foreach (Result<S3Object> item in ListAsync(layout.ObjectsPrefix, cancellationToken))
        {
            if (item.IsFailure)
            {
                yield return Result.Failure<StoredObjectInfo>(item.Error);
                yield break;
            }

            if (Name(item.Value) is { } name && layout.TryParseObject(name, out ContentHash? hash))
            {
                page.Add((hash, item.Value));
            }

            if (page.Count >= 1000)
            {
                foreach (Result<StoredObjectInfo> info in await DescribeAsync(page, cancellationToken))
                {
                    yield return info;
                    if (info.IsFailure)
                    {
                        yield break;
                    }
                }

                page.Clear();
            }
        }

        foreach (Result<StoredObjectInfo> info in await DescribeAsync(page, cancellationToken))
        {
            yield return info;
            if (info.IsFailure)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// With versioning on (or suspended): the non-current versions and delete markers left under the
    /// share's prefix, which S3 keeps until a lifecycle rule or someone removes them. 0 otherwise.
    /// </summary>
    public Task<Result<int>> CountRecoverableCopiesAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string prefix = ShareLayout.For(shareId).Prefix;

        return CallAsync(prefix, S3Operation.List, async (client, bucket) =>
        {
            GetBucketVersioningResponse versioning = await client.Api.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket.Name }, cancellationToken);
            string? status = versioning.VersioningConfig?.Status?.Value;
            if (string.IsNullOrEmpty(status) || string.Equals(status, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success(0);
            }

            int count = 0;
            ListVersionsRequest request = new() { BucketName = bucket.Name, Prefix = bucket.Key(prefix) };
            while (true)
            {
                ListVersionsResponse response = await client.Api.ListVersionsAsync(request, cancellationToken);
                count += (response.Versions ?? []).Count(version => version.IsDeleteMarker == true || version.IsLatest != true);
                if (response.IsTruncated != true)
                {
                    return Result.Success(count);
                }

                request.KeyMarker = response.NextKeyMarker;
                request.VersionIdMarker = response.NextVersionIdMarker;
            }
        }, cancellationToken);
    }

    public Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).ObjectBlob(hash), cancellationToken);

    /// <summary>
    /// The object itself, for an S3 publish target signing as the same identity (a CopyObject inside
    /// S3), and a presigned GET for any other target. The URL lives at most an hour, and never longer
    /// than temporary credentials that signed it (it stops working with them).
    /// </summary>
    public Task<Result<CopySource?>> GetCopySourceAsync(ShareId shareId, ContentHash hash, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync<CopySource?>(name, S3Operation.Read, async (client, bucket) =>
        {
            string key = bucket.Key(name);
            TimeSpan valid = lifetime < MaxPresignedLifetime ? lifetime : MaxPresignedLifetime;
            string url = await client.Api.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = bucket.Name,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = _timeProvider.GetUtcNow().Add(valid).UtcDateTime,
            });

            return Result.Success<CopySource?>(new CopySource(
                new Uri(url),
                new NativeObjectReference(ProviderName, bucket.Region, bucket.Name, key, client.CredentialScope)));
        }, cancellationToken);
    }

    public Task<Result<PublicationsSnapshot?>> GetPublicationsAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Publications;

        return CallAsync<PublicationsSnapshot?>(name, S3Operation.Read, async (client, bucket) =>
        {
            (byte[] Bytes, string ETag)? read = await GetBytesAsync(client, bucket, name, cancellationToken);
            if (read is not { } found)
            {
                return Result.Success<PublicationsSnapshot?>(null);
            }

            Result<PublicationRegistry> registry = _registrySerializer.Deserialize(found.Bytes);
            return registry.IsSuccess
                ? Result.Success<PublicationsSnapshot?>(new PublicationsSnapshot(registry.Value, found.ETag))
                : Result.Failure<PublicationsSnapshot?>(registry.Error);
        }, cancellationToken);
    }

    public Task<Result<string>> PutPublicationsAsync(ShareId shareId, PublicationRegistry registry, string? ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        S3Condition condition = ifMatchETag is null ? S3Condition.IfAbsent : S3Condition.IfMatch(ifMatchETag);

        // Written by someone else since it was read (or created, or removed): read again.
        return PutJsonAsync(ShareLayout.For(shareId).Publications, _registrySerializer.Serialize(registry), condition, name => StorageErrors.PreconditionFailed(name), cancellationToken);
    }

    public Task<Result> DeletePublicationsAsync(ShareId shareId, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).Publications, cancellationToken);

    /// <summary><c>Content-Encoding</c> as S3 returns it; <c>aws-chunked</c> (an upload's transfer framing) is not the object's.</summary>
    internal static Result<ObjectEncoding> EncodingOf(string? contentEncoding)
    {
        string remaining = string.Join(
            ",",
            (contentEncoding ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => !string.Equals(token, "aws-chunked", StringComparison.OrdinalIgnoreCase)));
        return ObjectEncodings.FromContentEncoding(remaining);
    }

    internal static Dictionary<string, string> ObjectMetadata(ContentHash hash, ObjectUpload upload, long? stored)
    {
        Dictionary<string, string> metadata = new()
        {
            ["sha256"] = hash.Value,
            ["size"] = upload.Size.ToString(CultureInfo.InvariantCulture),
        };
        if (upload.Encoding != ObjectEncoding.Identity && stored is { } length)
        {
            metadata["storedsize"] = length.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private async Task<Result<S3Written>> UploadOnceAsync(S3Client client, S3Bucket bucket, string name, string key, ContentHash hash, ObjectUpload upload, CancellationToken cancellationToken)
    {
        using HashingCountingStream hashing = new(upload.Content);
        bool raw = upload.Encoding == ObjectEncoding.Identity;

        Result Verify()
        {
            // The staged bytes are never committed unless both checks pass.
            Result verdict = upload.Verify();
            if (verdict.IsFailure)
            {
                return verdict;
            }

            var actual = ContentHash.FromBytes(hashing.Finish());
            if (raw && (actual != hash || hashing.Count != upload.Size))
            {
                S3Log.HashMismatch(_logger, name, actual.Value);
                return Result.Failure(StorageErrors.HashMismatch(name));
            }

            return Result.Success();
        }

        // A gzip object's storedsize is only known once it's read: a single PutObject carries it, a
        // multipart upload (whose metadata is set when it starts) doesn't. Listings have the stored
        // length either way; the metadata is for people reading the bucket.
        S3Headers Headers(long? stored) => new(ObjectContentType, upload.Encoding.ToContentEncoding(), Metadata: ObjectMetadata(hash, upload, stored));

        // Raw bytes have a known length (one part size); gzip doesn't, and is sized from the uncompressed length.
        return await _uploader.UploadAsync(
            client, bucket, key, hashing, Headers, S3Condition.IfAbsent, raw ? upload.Size : null, upload.Size, Verify, cancellationToken);
    }

    private async Task<Result<string>> PutJsonAsync(string name, byte[] bytes, S3Condition condition, Func<string, Error?> whenConditionFails, CancellationToken cancellationToken)
    {
        return await CallAsync(name, S3Operation.Write, async (client, bucket) =>
        {
            Result<S3Written> written = await _uploader.PutAsync(client, bucket, bucket.Key(name), bytes, new S3Headers(ManifestContentType), condition, cancellationToken);
            if (written.IsFailure)
            {
                return Result.Failure<string>(written.Error);
            }

            if (written.Value.Outcome == S3WriteOutcome.ConditionFailed)
            {
                return whenConditionFails(name) is { } error ? Result.Failure<string>(error) : Result.Success(string.Empty);
            }

            return Result.Success(written.Value.ETag ?? string.Empty);
        }, cancellationToken);
    }

    internal static async Task<(byte[] Bytes, string ETag)?> GetBytesAsync(S3Client client, S3Bucket bucket, string name, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await client.Api.GetObjectAsync(new GetObjectRequest { BucketName = bucket.Name, Key = bucket.Key(name) }, cancellationToken);
            using MemoryStream buffer = new();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return (buffer.ToArray(), response.ETag);
        }
        catch (AmazonS3Exception exception) when (S3ErrorMapper.IsMissingKey(exception))
        {
            return null;
        }
    }

    internal static async Task<GetObjectMetadataResponse?> HeadAsync(S3Client client, string bucketName, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await client.Api.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucketName, Key = key }, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (S3ErrorMapper.IsMissingKey(exception))
        {
            return null;
        }
    }

    private async Task<List<Result<StoredObjectInfo>>> DescribeAsync(List<(ContentHash Hash, S3Object Item)> page, CancellationToken cancellationToken)
    {
        var described = new Result<StoredObjectInfo>?[page.Count];
        if (page.Count == 0)
        {
            return [];
        }

        Result<bool> done = await CallAsync("(objects)", S3Operation.Read, async (client, bucket) =>
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, page.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    (ContentHash hash, S3Object item) = page[index];
                    GetObjectMetadataResponse? head = await HeadAsync(client, bucket.Name, item.Key, token);
                    if (head is null)
                    {
                        return; // deleted since the listing
                    }

                    Result<ObjectEncoding> encoding = EncodingOf(head.Headers.ContentEncoding);
                    described[index] = encoding.IsSuccess
                        ? new StoredObjectInfo(hash, item.Size ?? head.ContentLength, item.LastModified ?? head.LastModified ?? DateTime.MinValue, encoding.Value)
                        : Result.Failure<StoredObjectInfo>(encoding.Error);
                });
            return Result.Success(true);
        }, cancellationToken);

        return done.IsFailure ? [Result.Failure<StoredObjectInfo>(done.Error)] : [.. described.OfType<Result<StoredObjectInfo>>()];
    }

    private Task<Result> DeleteAsync(string name, CancellationToken cancellationToken) =>
        CallAsync(name, S3Operation.Delete, async (client, bucket) =>
        {
            // Unconditional deletes succeed whether or not the key exists.
            await client.Api.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket.Name, Key = bucket.Key(name) }, cancellationToken);
            return Result.Success();
        }, cancellationToken);

    private string? Name(S3Object item) => _connection.Bucket.IsSuccess ? _connection.Bucket.Value.LayoutName(item.Key) : null;

    private async IAsyncEnumerable<Result<S3Object>> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Result<S3Object> item in S3Listing.ListAsync(_connection, prefix, pageSize: null, _logger, cancellationToken))
        {
            yield return item;
        }
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

    private async Task<Result> CallAsync(string name, S3Operation operation, Func<S3Client, S3Bucket, Task<Result>> call, CancellationToken cancellationToken)
    {
        Result<bool> outcome = await CallAsync(name, operation, async (client, bucket) =>
        {
            Result result = await call(client, bucket);
            return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
        }, cancellationToken);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);
    }
}

/// <summary>SHA-256 and a byte count of what passes through, read once, start to end.</summary>
internal sealed class HashingCountingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long Count { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public byte[] Finish() => _sha256.GetHashAndReset();

    public override int Read(byte[] buffer, int offset, int count) => Track(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        return Track(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sha256.Dispose(); // not the inner stream: it's the caller's
        }

        base.Dispose(disposing);
    }

    private int Track(ReadOnlySpan<byte> read)
    {
        _sha256.AppendData(read);
        Count += read.Length;
        return read.Length;
    }
}
