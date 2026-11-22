using System.IO.Compression;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>
/// The one place objects are encoded and decoded (CLAUDE.md §9, §11). Every reader of file content
/// goes through <see cref="OpenDecodedAsync"/>; nothing else calls
/// <see cref="IShareStore.OpenObjectReadAsync"/> (a unit test checks the source).
/// </summary>
internal sealed class ObjectCodec(IShareStore store, IContentHasher hasher, SyncOptions options)
{
    /// <summary>
    /// The object's <b>uncompressed</b> content, decoded exactly once. The stream stops with
    /// <see cref="CorruptObjectException"/> (an <see cref="IOException"/> carrying
    /// <c>Storage.CorruptObject</c>) as soon as it decodes past <paramref name="entry"/>'s size, or
    /// when the gzip is invalid: a crafted object can't fill the disk. The caller still checks the
    /// SHA-256 of what it reads; a truncated object fails that check.
    /// </summary>
    public async Task<Result<Stream>> OpenDecodedAsync(ShareId shareId, FileEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Result<StoredObject> opened = await store.OpenObjectReadAsync(shareId, entry.Sha256, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<Stream>(opened.Error);
        }

        return new DecodedStream(opened.Value, entry.Size, ShareLayout.For(shareId).ObjectBlob(entry.Sha256));
    }

    /// <summary>
    /// The upload for one local file: raw, or gzip when the share's <paramref name="setting"/> and
    /// <see cref="CompressionPolicy"/> say so (a sample of up to 1 MiB decides). For gzip, the file
    /// is read once more from the start, through SHA-256 and the compressor, straight into the
    /// store; the store commits only if that hash and length match <paramref name="entry"/>.
    /// </summary>
    /// <param name="sampleSource">The file, seekable (<c>ILocalFolder.OpenReadAsync</c> guarantees it); read for the sample, then rewound.</param>
    /// <param name="content">What the upload reads: the same file, usually wrapped to report progress.</param>
    public async Task<PreparedUpload> PrepareUploadAsync(
        ShareId shareId, SharePath path, FileEntry entry, Stream sampleSource, Stream content, CompressionSetting setting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sampleSource);
        ArgumentNullException.ThrowIfNull(content);

        if (!CompressionPolicy.NeedsSample(setting, path, entry.Size))
        {
            return PreparedUpload.Raw(content, entry.Size);
        }

        CompressionSample sample = await SampleAsync(sampleSource, entry.Size, cancellationToken);
        sampleSource.Position = 0;
        if (CompressionPolicy.Decide(setting, path, entry.Size, sample) != ObjectEncoding.Gzip)
        {
            return PreparedUpload.Raw(content, entry.Size);
        }

        return PreparedUpload.Gzip(content, hasher, options.CompressionLevel, entry, ShareLayout.For(shareId).ObjectBlob(entry.Sha256));
    }

    private async Task<CompressionSample> SampleAsync(Stream source, long size, CancellationToken cancellationToken)
    {
        int length = (int)Math.Min(size, CompressionPolicy.SampleBytes);
        byte[] sample = new byte[length];
        int read = await source.ReadAtLeastAsync(sample, length, throwOnEndOfStream: false, cancellationToken);
        using MemoryStream compressed = new();
        await using (GZipStream gzip = new(compressed, options.CompressionLevel, leaveOpen: true))
        {
            await gzip.WriteAsync(sample.AsMemory(0, read), cancellationToken);
        }

        return new CompressionSample(read, compressed.Length);
    }
}

/// <summary>An upload ready for the store, and how many bytes it stored (known once the store has read it all).</summary>
internal sealed class PreparedUpload : IAsyncDisposable
{
    private readonly CompressingReadStream? _compressing;

    private PreparedUpload(Stream content, long size)
    {
        Upload = ObjectUpload.Raw(content, size);
    }

    private PreparedUpload(Stream content, IContentHasher hasher, CompressionLevel level, FileEntry entry, string blobName)
    {
        CompressingReadStream compressing = new(content, hasher, level);
        _compressing = compressing;

        // The verdict the store asks for after the last byte, before it commits anything.
        Upload = new ObjectUpload(compressing, ObjectEncoding.Gzip, entry.Size, () =>
            compressing.Finished && compressing.SourceLength == entry.Size && compressing.SourceHash == entry.Sha256
                ? Result.Success()
                : Result.Failure(StorageErrors.HashMismatch(blobName)));
    }

    public ObjectUpload Upload { get; }

    /// <summary>The bytes that went to the store: compressed for gzip, the file's size for raw.</summary>
    public long StoredBytes => _compressing?.ProducedLength ?? Upload.Size;

    public static PreparedUpload Raw(Stream content, long size) => new(content, size);

    public static PreparedUpload Gzip(Stream content, IContentHasher hasher, CompressionLevel level, FileEntry entry, string blobName) =>
        new(content, hasher, level, entry, blobName);

    public ValueTask DisposeAsync() => _compressing?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>A stored object that can't be what its name says, found while decoding. Carries <c>Storage.CorruptObject</c>.</summary>
internal sealed class CorruptObjectException(Error error) : IOException(error.Description)
{
    public Error Error { get; } = error;
}

/// <summary>Decoded bytes, at most <c>limit</c> of them; invalid gzip and anything past the limit are <see cref="CorruptObjectException"/>.</summary>
internal sealed class DecodedStream : Stream
{
    private readonly Stream inner;
    private readonly long limit;
    private readonly string blobName;
    private long _read;

    public DecodedStream(StoredObject stored, long limit, string blobName)
    {
        inner = stored.Encoding == ObjectEncoding.Gzip
            ? new GZipStream(stored.Content, CompressionMode.Decompress, leaveOpen: false)
            : stored.Content;
        this.limit = limit;
        this.blobName = blobName;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return Count(inner.Read(buffer));
        }
        catch (InvalidDataException exception)
        {
            throw Corrupt($"invalid gzip: {exception.Message}");
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return Count(await inner.ReadAsync(buffer, cancellationToken));
        }
        catch (InvalidDataException exception)
        {
            throw Corrupt($"invalid gzip: {exception.Message}");
        }
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
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        _read += read;
        return _read > limit ? throw Corrupt($"it decodes to more than the {limit} bytes the share lists") : read;
    }

    private CorruptObjectException Corrupt(string detail) => new(StorageErrors.CorruptObject(blobName, detail));
}

/// <summary>
/// Reads a file and yields it gzip-compressed, hashing the <b>uncompressed</b> bytes in the same
/// pass. Pull-based and bounded: one read buffer plus what the compressor has produced from it, so
/// nothing is written to disk and nothing large is held in memory.
/// </summary>
internal sealed class CompressingReadStream : Stream
{
    private const int BufferSize = 81920;

    private readonly Stream _source;
    private readonly IIncrementalContentHash _hash;
    private readonly MemoryStream _produced = new();
    private readonly GZipStream _gzip;
    private readonly byte[] _input = new byte[BufferSize];
    private int _offset;

    public CompressingReadStream(Stream source, IContentHasher hasher, CompressionLevel level)
    {
        _source = source;
        _hash = hasher.CreateIncremental();
        _gzip = new GZipStream(_produced, level, leaveOpen: true);
    }

    /// <summary>True once the source ended and the gzip trailer was written.</summary>
    public bool Finished { get; private set; }

    public long SourceLength { get; private set; }

    /// <summary>The SHA-256 of the uncompressed bytes; set when <see cref="Finished"/>.</summary>
    public ContentHash? SourceHash { get; private set; }

    public long ProducedLength { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => ProducedLength; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _produced.Length)
        {
            if (Finished)
            {
                return 0;
            }

            _produced.SetLength(0);
            _offset = 0;
            int read = await _source.ReadAsync(_input, cancellationToken);
            if (read == 0)
            {
                await _gzip.DisposeAsync(); // writes the final block and the trailer
                SourceHash = _hash.Finish();
                Finished = true;
            }
            else
            {
                _hash.Append(_input.AsSpan(0, read));
                SourceLength += read;
                await _gzip.WriteAsync(_input.AsMemory(0, read), cancellationToken);
            }
        }

        int count = (int)Math.Min(buffer.Length, _produced.Length - _offset);
        _produced.GetBuffer().AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        ProducedLength += count;
        return count;
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
            _gzip.Dispose();
            _produced.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
