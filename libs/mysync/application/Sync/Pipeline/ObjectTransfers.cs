using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Run;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>Moves file bytes between the local folder and the store, reporting progress in bytes.</summary>
internal sealed class ObjectTransfers(IShareStore store, ILocalFolder folder, IContentHasher hasher, ObjectCodec codec)
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Uploads files in parallel, skipping objects the store already has, compressing those the
    /// share's <paramref name="compression"/> setting and <see cref="CompressionPolicy"/> pick.
    /// Returns what was transferred and the paths that couldn't be uploaded (left out of this commit).
    /// </summary>
    public async Task<UploadOutcome> UploadAsync(
        ShareId shareId,
        IReadOnlyList<(SharePath Path, FileEntry Entry)> files,
        CompressionSetting compression,
        ISyncProgressReporter progress,
        int parallelism,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return new UploadOutcome(TransferTotals.None, new Dictionary<SharePath, SkippedPath>(), []);
        }

        progress.PhaseStarted(SyncPhase.Uploading);
        progress.TransfersPlanned(TransferDirection.Upload, files.Count, files.Sum(file => file.Entry.Size));

        Lock gate = new();
        int count = 0;
        long bytes = 0;
        long stored = 0;
        Dictionary<SharePath, SkippedPath> failed = [];
        HashSet<ContentHash> sent = [];

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                Result<long?> uploaded = await UploadOneAsync(shareId, file.Path, file.Entry, compression, progress, ct);

                lock (gate)
                {
                    if (uploaded.IsFailure)
                    {
                        failed[file.Path] = Skip(file.Path, uploaded.Error);
                    }
                    else if (uploaded.Value is { } storedBytes)
                    {
                        count++;
                        bytes += file.Entry.Size;
                        stored += storedBytes;
                        sent.Add(file.Entry.Sha256);
                    }
                }
            });

        return new UploadOutcome(new TransferTotals(count, bytes, stored), failed, sent);
    }

    /// <summary>
    /// Downloads an object into a new staged file, verifying its SHA-256 while it streams. On a
    /// mismatch the staged file is discarded and nothing reaches the folder.
    /// </summary>
    public Task<Result<IStagedFile>> DownloadToStagingAsync(
        ShareId shareId, SharePath path, FileEntry entry, ISyncProgressReporter progress, CancellationToken cancellationToken) =>
        DownloadToStagingAsync(shareId, path, entry, folder, progress, cancellationToken);

    /// <summary>The same download, staged in <paramref name="into"/> (pull's destination) instead of the share root.</summary>
    public async Task<Result<IStagedFile>> DownloadToStagingAsync(
        ShareId shareId, SharePath path, FileEntry entry, ILocalFolder into, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(into);

        Result<Stream> opened = await codec.OpenDecodedAsync(shareId, entry, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<IStagedFile>(opened.Error);
        }

        Result<IStagedFile> staging = await into.CreateStagedFileAsync(cancellationToken);
        if (staging.IsFailure)
        {
            await opened.Value.DisposeAsync();
            return staging;
        }

        IStagedFile staged = staging.Value;
        bool keep = false;
        progress.FileStarted(TransferDirection.Download, path);

        try
        {
            await using Stream source = opened.Value;
            using IIncrementalContentHash hash = hasher.CreateIncremental();
            byte[] buffer = new byte[BufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.Append(buffer.AsSpan(0, read));
                await staged.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress.BytesTransferred(TransferDirection.Download, read);
            }

            await staged.Content.FlushAsync(cancellationToken);

            if (hash.Finish() != entry.Sha256)
            {
                return Result.Failure<IStagedFile>(StorageErrors.CorruptObject(ShareLayout.For(shareId).ObjectBlob(entry.Sha256), "it doesn't match its hash"));
            }

            keep = true;
            return Result.Success(staged);
        }
        catch (CorruptObjectException exception)
        {
            // Invalid gzip, or more bytes than the share lists: skip this path, keep syncing the rest.
            return Result.Failure<IStagedFile>(exception.Error);
        }
        catch (IOException exception)
        {
            // The network or the disk failed mid-download: skip this path, keep syncing the rest.
            return Result.Failure<IStagedFile>(LocalFileErrors.WriteFailed(path.Value, exception.Message));
        }
        finally
        {
            if (!keep)
            {
                await staged.DisposeAsync();
            }
        }
    }

    private Task<Result<long?>> UploadOneAsync(
        ShareId shareId, SharePath path, FileEntry entry, CompressionSetting compression, ISyncProgressReporter progress, CancellationToken cancellationToken) =>
        UploadOneAsync(shareId, path, entry, ct => folder.OpenReadAsync(path, ct), compression, progress, cancellationToken);

    /// <summary>
    /// One object: nothing is sent if the store already has it (in whichever form); otherwise
    /// <paramref name="open"/> gives the file's bytes, which go to the store raw or compressed.
    /// Used by sync for the share's folder, and by push for files anywhere.
    /// </summary>
    /// <param name="open">A seekable stream that reports its length, read from the start.</param>
    /// <returns>The bytes sent, or <c>null</c> when the store already had the object (in whichever form).</returns>
    public async Task<Result<long?>> UploadOneAsync(
        ShareId shareId,
        SharePath path,
        FileEntry entry,
        Func<CancellationToken, Task<Result<Stream>>> open,
        CompressionSetting compression,
        ISyncProgressReporter progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(progress);

        Result<bool> exists = await store.ObjectExistsAsync(shareId, entry.Sha256, cancellationToken);
        if (exists.IsFailure)
        {
            return Result.Failure<long?>(exists.Error);
        }

        if (exists.Value)
        {
            progress.FileStarted(TransferDirection.Upload, path);
            progress.BytesTransferred(TransferDirection.Upload, entry.Size);
            progress.FileTransferred(TransferDirection.Upload, path);
            return Result.Success<long?>(null);
        }

        Result<Stream> opened = await open(cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<long?>(opened.Error);
        }

        // Progress counts the file's own bytes as they're read, so the bar matches its size.
        progress.FileStarted(TransferDirection.Upload, path);
        await using ProgressReportingStream content = new(opened.Value, progress, TransferDirection.Upload);
        Result uploaded;
        long storedBytes;
        try
        {
            await using PreparedUpload prepared = await codec.PrepareUploadAsync(shareId, path, entry, opened.Value, content, compression, cancellationToken);
            uploaded = await store.UploadObjectAsync(shareId, entry.Sha256, prepared.Upload, cancellationToken);
            storedBytes = prepared.StoredBytes;
        }
        catch (IOException exception)
        {
            // Reading the local file failed mid-upload (e.g. locked or removed): skip it this run.
            return Result.Failure<long?>(LocalFileErrors.Unreadable(path.Value, exception.Message));
        }

        if (uploaded.IsFailure)
        {
            return Result.Failure<long?>(uploaded.Error);
        }

        progress.FileTransferred(TransferDirection.Upload, path);
        return storedBytes;
    }

    internal static SkippedPath Skip(SharePath path, Error error) => error.Code switch
    {
        "Storage.HashMismatch" => new SkippedPath(path.Value, SkipCause.Unstable, "it changed while it was being uploaded"),
        "Local.Unreadable" => new SkippedPath(path.Value, SkipCause.Unreadable, error.Description),
        _ => new SkippedPath(path.Value, SkipCause.ApplyFailed, error.Description),
    };
}

/// <param name="Sent">Objects this run actually stored (not ones the store already had): removed again if the share turns out to be deleted.</param>
internal sealed record UploadOutcome(TransferTotals Uploaded, IReadOnlyDictionary<SharePath, SkippedPath> Failed, IReadOnlyCollection<ContentHash> Sent);
