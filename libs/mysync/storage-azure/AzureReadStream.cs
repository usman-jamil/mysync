using Microsoft.Extensions.Logging;

namespace MySync.Storage.Azure;

/// <summary>
/// An object download as the pipeline sees it: a failure mid-read is an <see cref="IOException"/>
/// with a message of ours (the pipeline skips that path), never an Azure exception whose text
/// could carry request details. Seekable, like the SDK's own read stream.
/// </summary>
internal sealed class AzureReadStream(Stream inner, string blobName, ILogger logger) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, CancellationToken.None))
        {
            throw Translate(exception);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            throw Translate(exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

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
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private IOException Translate(Exception exception)
    {
        int status = AzureErrorMapper.StatusOf(exception);
        AzureLog.RequestFailed(logger, blobName, status, ConnectionStringRedactor.Redact(exception.Message));
        return new IOException($"downloading '{blobName}' from storage failed ({AzureErrorMapper.Map(exception, blobName).Code}, status {status})");
    }
}
