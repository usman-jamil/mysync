namespace MySync.Testing;

/// <summary>
/// Serves the given bytes, then throws <see cref="IOException"/> once <paramref name="failAfter"/>
/// bytes have been read: a network drop mid-download, or a file locked mid-upload.
/// </summary>
public sealed class FailingStream(byte[] content, int failAfter) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
        get => _position;
        set => _position = (int)value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= failAfter)
        {
            throw new IOException("the connection was reset");
        }

        int take = Math.Min(Math.Min(buffer.Length, content.Length - _position), failAfter - _position);
        content.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => content.Length + offset,
    };

    public override void Flush()
    {
        // Read-only.
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
