using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Content;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Application.Conflicts.Diff;

/// <summary>
/// Where <c>diff</c>'s sides come from: the file on disk now, and stored objects (verified against
/// their hash before use). Reads at most <see cref="DiffLimits.MaxBytes"/> + 1 bytes of a side into
/// memory; a larger side is "too large" and never buffered. Nothing here writes.
/// </summary>
internal sealed class DiffSources(ILocalFolder folder, IShareStore store, IContentHasher hasher, ObjectCodec codec)
{
    private const int BufferSize = 81920;

    public async Task<Result<Manifest>> HeadAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<Manifest>(head.Error);
        }

        if (head.Value is not { Manifest: { } manifest })
        {
            return Result.Failure<Manifest>(ShareErrors.NotFound(shareId));
        }

        return manifest.Deletion is { } deletion ? Result.Failure<Manifest>(ShareErrors.Deleted(shareId, deletion)) : manifest;
    }

    /// <summary>
    /// The file on disk now, hashed in full. Its executable bit comes from <paramref name="known"/>
    /// when the content is the same (the folder abstraction doesn't report the bit), else false.
    /// </summary>
    public async Task<Result<Side>> LocalAsync(SharePath path, FileEntry? known, CancellationToken cancellationToken)
    {
        Result<LocalFile?> file = await folder.GetFileAsync(path, cancellationToken);
        if (file.IsFailure)
        {
            return Result.Failure<Side>(file.Error);
        }

        if (file.Value is not { } found)
        {
            return Side.Absent(DiffSideKind.Local);
        }

        Result<Stream> opened = await folder.OpenReadAsync(path, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<Side>(opened.Error);
        }

        try
        {
            await using Stream content = opened.Value;
            (ContentHash hash, byte[]? bytes, long length) = await ReadAsync(content, cancellationToken);
            bool executable = known is not null && known.Sha256 == hash && known.Executable;
            Result<FileEntry> entry = FileEntry.Create(hash, length, found.MtimeUtc, executable);
            return entry.IsFailure
                ? Result.Failure<Side>(entry.Error)
                : new Side(DiffSideKind.Local, entry.Value, length, _ => Task.FromResult(Result.Success(bytes)), null, null);
        }
        catch (IOException exception)
        {
            return Result.Failure<Side>(LocalFileErrors.Unreadable(path.Value, exception.Message));
        }
    }

    /// <summary>A stored version (remote or base): read lazily, verified when it is.</summary>
    public Side Stored(ShareId shareId, SharePath path, DiffSideKind kind, ManifestEntry? entry, Domain.Manifests.Revision? revision = null, string? updatedBy = null)
    {
        if (entry is not FileEntry file)
        {
            return new Side(kind, entry, null, _ => Task.FromResult(Result.Success<byte[]?>([])), revision, updatedBy);
        }

        return new Side(kind, file, file.Size, cancellationToken => ReadStoredAsync(shareId, path, kind, file, cancellationToken), revision, updatedBy);
    }

    /// <summary>A stored version as a stream for <c>--print</c>: unlimited, its hash checked when it ends.</summary>
    public async Task<Result<Stream>> OpenStoredAsync(ShareId shareId, SharePath path, DiffSideKind kind, FileEntry file, CancellationToken cancellationToken)
    {
        Result<Stream> opened = await codec.OpenDecodedAsync(shareId, file, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<Stream>(NotStored(opened.Error, path, kind));
        }

        return new VerifyingReadStream(opened.Value, hasher.CreateIncremental(), file.Sha256, ShareLayout.For(shareId).ObjectBlob(file.Sha256));
    }

    private async Task<Result<byte[]?>> ReadStoredAsync(ShareId shareId, SharePath path, DiffSideKind kind, FileEntry file, CancellationToken cancellationToken)
    {
        if (file.Size > DiffLimits.MaxBytes)
        {
            // Not read at all: only whether the share still has it matters.
            Result<bool> exists = await store.ObjectExistsAsync(shareId, file.Sha256, cancellationToken);
            if (exists.IsFailure)
            {
                return Result.Failure<byte[]?>(exists.Error);
            }

            return exists.Value ? Result.Success<byte[]?>(null) : Result.Failure<byte[]?>(DiffErrors.VersionNotStored(path.Value, DiffQueryHandler.Name(kind)));
        }

        Result<Stream> opened = await codec.OpenDecodedAsync(shareId, file, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<byte[]?>(NotStored(opened.Error, path, kind));
        }

        await using Stream content = opened.Value;
        ContentHash hash;
        byte[]? bytes;
        try
        {
            (hash, bytes, _) = await ReadAsync(content, cancellationToken);
        }
        catch (CorruptObjectException exception)
        {
            return Result.Failure<byte[]?>(exception.Error);
        }
        return hash == file.Sha256
            ? Result.Success(bytes)
            : Result.Failure<byte[]?>(StorageErrors.CorruptObject(ShareLayout.For(shareId).ObjectBlob(file.Sha256), "it doesn't match its hash"));
    }

    private static Error NotStored(Error error, SharePath path, DiffSideKind kind) =>
        error.Code == "Storage.NotFound" ? DiffErrors.VersionNotStored(path.Value, DiffQueryHandler.Name(kind)) : error;

    /// <summary>Hashes the whole stream; keeps its bytes only while they fit the limit.</summary>
    private async Task<(ContentHash Hash, byte[]? Bytes, long Length)> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        using IIncrementalContentHash hash = hasher.CreateIncremental();
        using MemoryStream kept = new();
        byte[] buffer = new byte[BufferSize];
        long length = 0;
        bool tooLarge = false;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.Append(buffer.AsSpan(0, read));
            length += read;
            if (!tooLarge)
            {
                if (length > DiffLimits.MaxBytes)
                {
                    tooLarge = true;
                    kept.SetLength(0);
                }
                else
                {
                    await kept.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }

        return (hash.Finish(), tooLarge ? null : kept.ToArray(), length);
    }
}

/// <summary>One side of a comparison: its entry (null = absent) and a way to read its bytes (null bytes = too large).</summary>
internal sealed record Side(
    DiffSideKind Kind,
    ManifestEntry? Entry,
    long? Size,
    Func<CancellationToken, Task<Result<byte[]?>>> ReadAsync,
    Domain.Manifests.Revision? Revision,
    string? UpdatedBy)
{
    public static Side Absent(DiffSideKind kind) => new(kind, null, null, _ => Task.FromResult(Result.Success<byte[]?>([])), null, null);

    public DiffSide? Info => Entry switch
    {
        FileEntry file => new DiffSide(EntryType.File, file.Sha256, file.Size, file.MtimeUtc, file.Executable, Revision, UpdatedBy),
        DirectoryEntry => new DiffSide(EntryType.Directory, null, null, null, false, Revision, UpdatedBy),
        _ => null,
    };

    /// <summary>Same content as <paramref name="recorded"/> (a recorded conflict side).</summary>
    public bool Matches(ManifestEntry? recorded) => (Entry, recorded) switch
    {
        (null, null) => true,
        (FileEntry now, FileEntry then) => now.Sha256 == then.Sha256,
        (null, DirectoryEntry) => true, // the folder abstraction only sees files
        _ => false,
    };
}

/// <summary>Passes a stored object through, hashing it; at the end, a mismatch is an <see cref="IOException"/>.</summary>
internal sealed class VerifyingReadStream(Stream inner, IIncrementalContentHash hash, ContentHash expected, string blobName) : Stream
{
    private bool _checked;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count), buffer.AsSpan(offset));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Check(await inner.ReadAsync(buffer, cancellationToken), buffer.Span);

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
            hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Check(int read, ReadOnlySpan<byte> buffer)
    {
        if (read > 0)
        {
            hash.Append(buffer[..read]);
        }
        else if (!_checked)
        {
            _checked = true;
            if (hash.Finish() != expected)
            {
                throw new IOException(StorageErrors.CorruptObject(blobName, "it doesn't match its hash").Description);
            }
        }

        return read;
    }
}
