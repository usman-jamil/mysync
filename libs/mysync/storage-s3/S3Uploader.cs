using System.Buffers;
using System.Security.Cryptography;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>The headers and metadata an upload stores with the object.</summary>
internal sealed record S3Headers(
    string ContentType,
    string? ContentEncoding = null,
    string? ContentDisposition = null,
    string? CacheControl = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>The condition a write carries.</summary>
internal abstract record S3Condition
{
    public static readonly S3Condition None = new Unconditional();

    /// <summary><c>If-None-Match: *</c>: only if the key doesn't exist.</summary>
    public static readonly S3Condition IfAbsent = new Absent();

    public static S3Condition IfMatch(string etag) => new Matching(etag);

    internal sealed record Unconditional : S3Condition;

    internal sealed record Absent : S3Condition;

    internal sealed record Matching(string ETag) : S3Condition;
}

internal enum S3WriteOutcome
{
    /// <summary>Written; <see cref="S3Written.ETag"/> is the new object's.</summary>
    Stored,

    /// <summary>412 (or 404 for <c>If-Match</c> on a missing key): the condition is false. Never retried.</summary>
    ConditionFailed,

    /// <summary>
    /// 409 ConditionalRequestConflict on CompleteMultipartUpload: the upload is dead and has been
    /// aborted. The caller decides whether to restart it (it needs the content again).
    /// </summary>
    Conflict,
}

/// <param name="Length">The bytes read from the content (and stored, when <see cref="Outcome"/> is <see cref="S3WriteOutcome.Stored"/>).</param>
internal sealed record S3Written(S3WriteOutcome Outcome, string? ETag, long Length);

/// <summary>
/// Writes an object of any length, known or not, holding at most one part in memory (CLAUDE.md
/// §11, "S3 differences").
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Up to one part is read first. <b>If the content ends inside it, one PutObject</b> carries it,
/// with the condition. Otherwise a multipart upload goes on part by part (<see cref="S3PartSizes"/>),
/// and the condition goes on CompleteMultipartUpload.</item>
/// <item><paramref name="beforeCommit"/> (the hash check) runs after the last byte and before the
/// PutObject or the completion, so nothing is ever stored that fails it.</item>
/// <item><b>409 ConditionalRequestConflict</b> on a PutObject is retried as is (the next try gets a
/// real answer); on a completion the upload is aborted and <see cref="S3WriteOutcome.Conflict"/>
/// returned. A 409 never reaches the application as a lost race.</item>
/// <item>A failed or cancelled multipart upload is <b>aborted</b> (best effort), so its parts aren't
/// left behind and billed; the setup guide's lifecycle rule catches what an abort can't.</item>
/// <item>No ACL is ever sent: new buckets have ACLs disabled.</item>
/// </list>
/// </remarks>
internal sealed class S3Uploader(S3TransferSettings settings, TimeProvider timeProvider, ILogger logger)
{
    /// <param name="headers">The headers, given the stored length when it's known before writing (one PutObject), <c>null</c> for a multipart upload.</param>
    /// <param name="knownLength">The exact length, when known (raw bytes): every part is one size.</param>
    /// <param name="sizeHint">A likely upper bound when the length isn't known (a gzip stream's uncompressed size): sizes the first part.</param>
    /// <param name="beforeCommit">Runs once after the last byte; a failure means nothing is stored (and is returned).</param>
    public async Task<Result<S3Written>> UploadAsync(
        S3Client client,
        S3Bucket bucket,
        string key,
        Stream content,
        Func<long?, S3Headers> headers,
        S3Condition condition,
        long? knownLength,
        long sizeHint,
        Func<Result>? beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(headers);

        if (knownLength > S3Limits.MaxObjectSize)
        {
            return Result.Failure<S3Written>(StorageErrors.TooLarge(key, S3Limits.MaxObjectSize));
        }

        long first = Math.Min(S3PartSizes.ForKnownLength(knownLength ?? Math.Max(sizeHint, 0), settings.PartSize), S3Limits.MaxBufferedPartSize);
        PartReader reader = new(content);
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)first);
        try
        {
            int read = await reader.ReadAsync(buffer, (int)first, cancellationToken);
            if (reader.Ended)
            {
                Result verdict = beforeCommit?.Invoke() ?? Result.Success();
                return verdict.IsFailure
                    ? Result.Failure<S3Written>(verdict.Error)
                    : await PutAsync(client, bucket, key, new ReadOnlyMemory<byte>(buffer, 0, read), headers(read), condition, cancellationToken);
            }

            if (condition is S3Condition.Matching)
            {
                throw new InvalidOperationException("If-Match writes are small objects (manifests, records); they never need a multipart upload.");
            }

            return await MultipartAsync(client, bucket, key, reader, buffer, read, first, knownLength is not null, headers(null), condition, beforeCommit, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>One PutObject from bytes in memory, retried on 409 ConditionalRequestConflict.</summary>
    public async Task<Result<S3Written>> PutAsync(
        S3Client client, S3Bucket bucket, string key, ReadOnlyMemory<byte> bytes, S3Headers headers, S3Condition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(headers);

        int attempt = 0;
        while (true)
        {
            attempt++;
            PutObjectRequest request = new()
            {
                BucketName = bucket.Name,
                Key = key,
                InputStream = new ReadOnlyMemoryStream(bytes),
                ContentType = headers.ContentType,
                AutoCloseStream = true,
            };
            Apply(request.Headers, request.Metadata, headers);
            switch (condition)
            {
                case S3Condition.Absent:
                    request.IfNoneMatch = "*";
                    break;
                case S3Condition.Matching matching:
                    request.IfMatch = matching.ETag;
                    break;
            }

            try
            {
                PutObjectResponse response = await client.Api.PutObjectAsync(request, cancellationToken);
                return new S3Written(S3WriteOutcome.Stored, response.ETag, bytes.Length);
            }
            catch (Exception exception) when (IsConditionFalse(exception, condition))
            {
                return new S3Written(S3WriteOutcome.ConditionFailed, null, bytes.Length);
            }
            catch (Exception exception) when (S3ErrorMapper.IsConditionalConflict(exception))
            {
                S3Log.ConditionalConflict(logger, key, attempt);
                if (attempt >= settings.ConditionalConflictAttempts)
                {
                    return Result.Failure<S3Written>(StorageErrors.Unreachable);
                }

                await Task.Delay(Backoff(attempt), timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The pause before try <paramref name="attempt"/> + 1: doubling from the first delay, up to 5 s,
    /// at 50–100% of that (jitter, so racing machines spread out).
    /// </summary>
    public TimeSpan Backoff(int attempt)
    {
        double full = Math.Min(settings.FirstConflictDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), 5000);
        return TimeSpan.FromMilliseconds(full * (50 + RandomNumberGenerator.GetInt32(51)) / 100);
    }

    internal static void Apply(HeadersCollection target, MetadataCollection metadata, S3Headers headers)
    {
        if (headers.ContentEncoding is { } encoding)
        {
            target.ContentEncoding = encoding;
        }

        if (headers.ContentDisposition is { } disposition)
        {
            target.ContentDisposition = disposition;
        }

        if (headers.CacheControl is { } cacheControl)
        {
            target.CacheControl = cacheControl;
        }

        foreach ((string name, string value) in headers.Metadata ?? new Dictionary<string, string>())
        {
            metadata.Add(name, value);
        }
    }

    private static bool IsConditionFalse(Exception exception, S3Condition condition) => condition switch
    {
        S3Condition.Absent => S3ErrorMapper.IsPreconditionFailed(exception),
        S3Condition.Matching => S3ErrorMapper.IsPreconditionFailed(exception) || S3ErrorMapper.IsMissingKey(exception), // gone since read
        _ => false,
    };

    private async Task<Result<S3Written>> MultipartAsync(
        S3Client client,
        S3Bucket bucket,
        string key,
        PartReader reader,
        byte[] firstBuffer,
        int firstRead,
        long first,
        bool fixedSize,
        S3Headers headers,
        S3Condition condition,
        Func<Result>? beforeCommit,
        CancellationToken cancellationToken)
    {
        InitiateMultipartUploadRequest start = new() { BucketName = bucket.Name, Key = key, ContentType = headers.ContentType };
        Apply(start.Headers, start.Metadata, headers);
        string uploadId = (await client.Api.InitiateMultipartUploadAsync(start, cancellationToken)).UploadId;

        byte[] buffer = firstBuffer;
        bool ownBuffer = false;
        try
        {
            List<PartETag> parts = [];
            long total = 0;
            int read = firstRead;
            int part = 0;
            while (true)
            {
                part++;
                UploadPartResponse uploaded = await client.Api.UploadPartAsync(
                    new UploadPartRequest
                    {
                        BucketName = bucket.Name,
                        Key = key,
                        UploadId = uploadId,
                        PartNumber = part,
                        InputStream = new ReadOnlyMemoryStream(new ReadOnlyMemory<byte>(buffer, 0, read)),
                    },
                    cancellationToken);
                parts.Add(new PartETag(part, uploaded.ETag));
                total += read;

                if (reader.Ended)
                {
                    break;
                }

                long size = fixedSize ? first : S3PartSizes.ForPart(part + 1, first);
                if (part + 1 > S3Limits.MaxParts || total + 1 > S3Limits.MaxObjectSize)
                {
                    await AbortAsync(client, bucket, key, uploadId);
                    return Result.Failure<S3Written>(StorageErrors.TooLarge(key, Math.Min(total, S3Limits.MaxObjectSize)));
                }

                if (size > buffer.Length)
                {
                    if (ownBuffer)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    buffer = ArrayPool<byte>.Shared.Rent((int)size);
                    ownBuffer = true;
                }

                read = await reader.ReadAsync(buffer, (int)size, cancellationToken);
            }

            Result verdict = beforeCommit?.Invoke() ?? Result.Success();
            if (verdict.IsFailure)
            {
                await AbortAsync(client, bucket, key, uploadId);
                return Result.Failure<S3Written>(verdict.Error);
            }

            CompleteMultipartUploadRequest complete = new() { BucketName = bucket.Name, Key = key, UploadId = uploadId, PartETags = parts };
            if (condition is S3Condition.Absent)
            {
                complete.IfNoneMatch = "*";
            }

            try
            {
                CompleteMultipartUploadResponse completed = await client.Api.CompleteMultipartUploadAsync(complete, cancellationToken);
                return new S3Written(S3WriteOutcome.Stored, completed.ETag, total);
            }
            catch (Exception exception) when (condition is S3Condition.Absent && S3ErrorMapper.IsPreconditionFailed(exception))
            {
                await AbortAsync(client, bucket, key, uploadId);
                return new S3Written(S3WriteOutcome.ConditionFailed, null, total);
            }
            catch (Exception exception) when (S3ErrorMapper.IsConditionalConflict(exception))
            {
                // The upload can't be completed any more: retrying the completion is wrong.
                S3Log.ConditionalConflict(logger, key, 1);
                await AbortAsync(client, bucket, key, uploadId);
                return new S3Written(S3WriteOutcome.Conflict, null, total);
            }
        }
        catch (Exception)
        {
            await AbortAsync(client, bucket, key, uploadId); // failed or cancelled: don't leave billed parts behind
            throw;
        }
        finally
        {
            if (ownBuffer)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task AbortAsync(S3Client client, S3Bucket bucket, string key, string uploadId)
    {
        try
        {
            await client.Api.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = bucket.Name, Key = key, UploadId = uploadId }, CancellationToken.None);
        }
        catch (Exception exception) when (S3ErrorMapper.IsStorageFailure(exception, CancellationToken.None))
        {
            S3Log.AbortFailed(logger, key, S3Redactor.Redact(exception.Message));
        }
    }

    /// <summary>Reads parts, and knows when the content has ended without reading a part past it (one byte of look-ahead).</summary>
    private sealed class PartReader(Stream content)
    {
        private int _pending = -1;

        public bool Ended { get; private set; }

        public async Task<int> ReadAsync(byte[] buffer, int size, CancellationToken cancellationToken)
        {
            int filled = 0;
            if (_pending >= 0)
            {
                buffer[0] = (byte)_pending;
                _pending = -1;
                filled = 1;
            }

            int read;
            while (filled < size && (read = await content.ReadAsync(buffer.AsMemory(filled, size - filled), cancellationToken)) > 0)
            {
                filled += read;
            }

            if (filled < size)
            {
                Ended = true;
                return filled;
            }

            byte[] next = new byte[1];
            if (await content.ReadAsync(next, cancellationToken) == 0)
            {
                Ended = true;
            }
            else
            {
                _pending = next[0];
            }

            return filled;
        }
    }
}

/// <summary>A seekable, read-only view of bytes already in memory (the SDK retries a request by seeking back).</summary>
internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> bytes) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => bytes.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, bytes.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, bytes.Length - _position);
        bytes.Span.Slice((int)_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => bytes.Length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
