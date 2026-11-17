using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>
/// The commit protocol (CLAUDE.md §6): read HEAD, let the caller plan against it, write the new
/// manifest only if HEAD is unchanged (<c>If-Match</c>). If another machine committed first,
/// re-read and re-plan, up to <see cref="SyncOptions.MaxCommitAttempts"/> times with jittered
/// exponential back-off. Objects uploaded by an earlier attempt stay valid.
/// </summary>
internal sealed class RemoteCommitter(
    IShareStore store,
    SyncOptions options,
    TimeProvider timeProvider,
    ILogger<RemoteCommitter> logger)
{
    /// <param name="prepare">
    /// Plans against the HEAD it is given and returns the manifest to commit, or <c>null</c> if
    /// there is nothing to commit. Called once per attempt.
    /// </param>
    public async Task<Result<CommitOutcome>> CommitAsync(
        ShareId shareId,
        Func<ManifestSnapshot, CancellationToken, Task<Result<Manifest?>>> prepare,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= options.MaxCommitAttempts; attempt++)
        {
            Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
            if (head.IsFailure)
            {
                return Result.Failure<CommitOutcome>(head.Error);
            }

            if (head.Value is not { } snapshot)
            {
                return Result.Failure<CommitOutcome>(ShareErrors.NotFound(shareId));
            }

            if (snapshot.Manifest.ShareId != shareId)
            {
                return Result.Failure<CommitOutcome>(ShareErrors.InvalidManifest($"it belongs to share {snapshot.Manifest.ShareId}, not {shareId}."));
            }

            Result<Manifest?> prepared = await prepare(snapshot, cancellationToken);
            if (prepared.IsFailure)
            {
                return Result.Failure<CommitOutcome>(prepared.Error);
            }

            if (prepared.Value is not { } manifest)
            {
                return new CommitOutcome(snapshot, null, attempt, HistoryWritten: false);
            }

            Result<string> replaced = await store.ReplaceManifestAsync(shareId, manifest, snapshot.ETag, cancellationToken);

            if (replaced.IsSuccess)
            {
                bool history = await WriteHistoryAsync(shareId, manifest, cancellationToken);
                return new CommitOutcome(snapshot, manifest, attempt, history);
            }

            if (replaced.Error.Code != "Storage.PreconditionFailed")
            {
                return Result.Failure<CommitOutcome>(replaced.Error);
            }

            logger.LogInformation("Share {ShareId} changed while committing (attempt {Attempt}); re-planning", shareId, attempt);

            if (attempt < options.MaxCommitAttempts)
            {
                await Task.Delay(BackOff(attempt), timeProvider, cancellationToken);
            }
        }

        return Result.Failure<CommitOutcome>(SyncErrors.CommitRetriesExhausted(options.MaxCommitAttempts));
    }

    /// <summary>Best effort (CLAUDE.md §6 step 4): a failure only logs a warning.</summary>
    public async Task<bool> WriteHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        Result history = await store.PutManifestHistoryAsync(shareId, manifest, cancellationToken);

        if (history.IsFailure)
        {
            logger.LogWarning("Could not write history for revision {Revision}: {Error}", manifest.Revision, history.Error.Code);
        }

        return history.IsSuccess;
    }

    /// <summary>base × 2^(attempt-1), scaled by a random 50–150%.</summary>
    internal TimeSpan BackOff(int attempt)
    {
        double exponential = options.CommitRetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        int jitterPercent = RandomNumberGenerator.GetInt32(50, 151);

        return TimeSpan.FromMilliseconds(exponential * jitterPercent / 100);
    }
}

/// <param name="Remote">The HEAD the final plan was made against.</param>
/// <param name="Committed">The manifest written, or <c>null</c> if there was nothing to commit.</param>
internal sealed record CommitOutcome(ManifestSnapshot Remote, Manifest? Committed, int Attempts, bool HistoryWritten);
