using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace MySync.Storage.S3;

/// <summary>
/// An object download as the pipeline sees it: a failure mid-read is an <see cref="IOException"/>
/// with a message of ours (the pipeline skips that path), never an AWS exception whose text could
/// carry request details. Disposing it disposes the response (the connection goes back to the pool).
/// </summary>
internal sealed class S3ReadStream(GetObjectResponse response, string key, ILogger logger) : Stream
{
    private readonly Stream _inner = response.ResponseStream;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => response.ContentLength;

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (IsTransferFailure(exception, CancellationToken.None))
        {
            throw Translate(exception);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (IsTransferFailure(exception, cancellationToken))
        {
            throw Translate(exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
        // Read-only.
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        response.Dispose();
        return base.DisposeAsync();
    }

    // The SDK's checksum validation and the network fail as these mid-stream.
    private static bool IsTransferFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is IOException || S3ErrorMapper.IsStorageFailure(exception, cancellationToken);

    private IOException Translate(Exception exception)
    {
        S3Log.RequestFailed(logger, key, S3ErrorMapper.StatusOf(exception), S3ErrorMapper.CodeOf(exception), S3Redactor.Redact(exception.Message));
        return new IOException($"downloading '{key}' from storage failed ({S3ErrorMapper.CodeOf(exception)})");
    }
}
