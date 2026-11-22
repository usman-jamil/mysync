using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Sync.Push;

/// <summary>
/// Reads push's files and uploads them (CLAUDE.md §10b). Servers are busy: a log may grow while it
/// is pushed. Each file is <b>captured</b>: its length is taken when it is opened, exactly that many
/// bytes are hashed, and later exactly that many are uploaded under that hash, so a file that grew
/// meanwhile is stored as it was when it was opened. A file that shrank or was replaced (fewer bytes
/// than captured, or different ones: the store refuses a hash mismatch) is captured again, up to
/// <see cref="MaxReadAttempts"/> times, then skipped as unstable.
/// </summary>
internal sealed class PushFiles(ILocalSources sources, IContentHasher hasher, IShareStore store, ObjectTransfers transfers)
{
    internal const int MaxReadAttempts = 3;

    private const string UnstableCode = "Push.Unstable";

    /// <param name="shareId">Where to upload, or check what is stored; <c>null</c> for a share that doesn't exist yet (a dry run of <c>--create</c>).</param>
    /// <param name="upload">False for a dry run: files are read and hashed, nothing is sent.</param>
    public async Task<PushFilesOutcome> RunAsync(
        ShareId? shareId,
        IReadOnlyList<SourceFile> files,
        CompressionSetting compression,
        bool upload,
        ISyncProgressReporter progress,
        int parallelism,
        CancellationToken cancellationToken)
    {
        if (upload)
        {
            progress.PhaseStarted(SyncPhase.Uploading);
            progress.TransfersPlanned(TransferDirection.Upload, files.Count, files.Sum(file => file.Size));
        }

        ContentHash empty = await hasher.HashAsync(Stream.Null, cancellationToken);
        Lock gate = new();
        List<CapturedFile> captured = [];
        List<SkippedPath> skipped = [];
        HashSet<ContentHash> sent = [];

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                Result<CapturedFile> one = await OneAsync(shareId, file, empty, compression, upload, progress, ct);

                lock (gate)
                {
                    if (one.IsFailure)
                    {
                        skipped.Add(one.Error.Code == UnstableCode
                            ? new SkippedPath(file.Path.Value, SkipCause.Unstable, one.Error.Description)
                            : ObjectTransfers.Skip(file.Path, one.Error));
                    }
                    else
                    {
                        captured.Add(one.Value);
                        if (one.Value.Uploaded && upload)
                        {
                            sent.Add(one.Value.Entry.Sha256);
                        }
                    }
                }
            });

        return new PushFilesOutcome(
            [.. captured.OrderBy(file => file.Path, SharePath.OrdinalComparer)],
            [.. skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)],
            sent);
    }

    private async Task<Result<CapturedFile>> OneAsync(
        ShareId? shareId, SourceFile file, ContentHash empty, CompressionSetting compression, bool upload, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        string detail = "it changed while it was read";

        for (int attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            Result<FileEntry?> read = await CaptureAsync(file, empty, cancellationToken);
            if (read.IsFailure)
            {
                return Result.Failure<CapturedFile>(read.Error);
            }

            if (read.Value is not { } entry)
            {
                detail = "it shrank or was replaced while it was read";
                continue;
            }

            if (shareId is null)
            {
                return new CapturedFile(file.Path, entry, Uploaded: true, entry.Size);
            }

            if (!upload)
            {
                Result<bool> stored = await store.ObjectExistsAsync(shareId, entry.Sha256, cancellationToken);
                if (stored.IsFailure)
                {
                    return Result.Failure<CapturedFile>(stored.Error);
                }

                long wouldSend = stored.Value ? 0 : entry.Size;
                return new CapturedFile(file.Path, entry, Uploaded: !stored.Value, wouldSend);
            }

            Result<long?> sent = await transfers.UploadOneAsync(
                shareId, file.Path, entry, ct => OpenCapturedAsync(file.FullPath, entry.Size, ct), compression, progress, cancellationToken);
            if (sent.IsSuccess)
            {
                return new CapturedFile(file.Path, entry, sent.Value is not null, sent.Value ?? 0);
            }

            if (sent.Error.Code != "Storage.HashMismatch")
            {
                return Result.Failure<CapturedFile>(sent.Error);
            }

            detail = "it changed while it was uploaded";
        }

        // Not an Error of the catalog: it never leaves this class, it becomes a skip.
        return Result.Failure<CapturedFile>(Error.Problem(
            UnstableCode, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{detail}, {MaxReadAttempts} times")));
    }

    /// <returns>The entry for exactly the bytes that were there when the file was opened, or <c>null</c> if fewer were left to read.</returns>
    private async Task<Result<FileEntry?>> CaptureAsync(SourceFile file, ContentHash empty, CancellationToken cancellationToken)
    {
        // An empty file is never opened: FIFOs, sockets and devices report size 0 and could block a read.
        if (file.Size == 0)
        {
            return FileEntry.Create(empty, 0, file.MtimeUtc, file.Executable).Value;
        }

        Result<OpenedSourceFile> opened = await sources.OpenAsync(file.FullPath, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<FileEntry?>(Unreadable(file, opened.Error));
        }

        try
        {
            await using OpenedSourceFile handle = opened.Value;
            long length = handle.Content.Length;
            await using CapturedStream captured = new(handle.Content, length, leaveOpen: true);
            ContentHash hash = await hasher.HashAsync(captured, cancellationToken);
            if (captured.Position != length)
            {
                return Result.Success<FileEntry?>(null);
            }

            Result<FileEntry> entry = FileEntry.Create(hash, length, handle.MtimeUtc, file.Executable);
            return entry.IsSuccess ? entry.Value : Result.Failure<FileEntry?>(entry.Error);
        }
        catch (IOException exception)
        {
            return Result.Failure<FileEntry?>(LocalFileErrors.Unreadable(file.Path.Value, exception.Message));
        }
    }

    private async Task<Result<Stream>> OpenCapturedAsync(string fullPath, long length, CancellationToken cancellationToken)
    {
        Result<OpenedSourceFile> opened = await sources.OpenAsync(fullPath, cancellationToken);
        return opened.IsSuccess
            ? new CapturedStream(opened.Value.Content, length, leaveOpen: false)
            : Result.Failure<Stream>(opened.Error);
    }

    private static Error Unreadable(SourceFile file, Error error) =>
        error.Code == "Local.Unreadable" ? LocalFileErrors.Unreadable(file.Path.Value, Detail(error)) : error;

    private static string Detail(Error error)
    {
        int colon = error.Description.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? error.Description : error.Description[(colon + 2)..];
    }
}

/// <param name="Path">Relative to the target folder.</param>
/// <param name="Entry">What was captured: the hash and size of exactly the bytes read.</param>
/// <param name="Uploaded">True if this run sent it (or, in a dry run, would); false if the share already stored it.</param>
/// <param name="StoredBytes">What went over the wire (compressed size for a gzip object).</param>
internal sealed record CapturedFile(SharePath Path, FileEntry Entry, bool Uploaded, long StoredBytes);

/// <param name="Sent">Objects this run stored: removed again if the share turns out to be deleted.</param>
internal sealed record PushFilesOutcome(IReadOnlyList<CapturedFile> Files, IReadOnlyList<SkippedPath> Skipped, IReadOnlyCollection<ContentHash> Sent);

/// <summary>
/// The first <c>length</c> bytes of a stream, however much more is written to it meanwhile. Seekable
/// and reports <see cref="Length"/> when the inner stream is seekable, as uploads need.
/// </summary>
internal sealed class CapturedStream(Stream inner, long length, bool leaveOpen) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => Math.Min(inner.Position, length);
        set => inner.Position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Allowed(buffer.Length)]);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };
        return Position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private int Allowed(int requested) => (int)Math.Clamp(length - inner.Position, 0, requested);
}
