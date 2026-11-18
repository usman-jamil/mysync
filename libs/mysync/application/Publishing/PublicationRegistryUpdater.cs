using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Publishing;

/// <summary>
/// Reads and changes <c>publications.json</c> (private account). Every change is a
/// read-modify-write guarded by the blob's ETag, retried with the manifest's back-off when
/// another machine wrote first.
/// </summary>
internal sealed class PublicationRegistryUpdater(IShareStore store, RemoteCommitter committer, SyncOptions options, TimeProvider timeProvider)
{
    public async Task<Result<PublicationRegistry>> ReadAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        Result<PublicationsSnapshot?> read = await store.GetPublicationsAsync(shareId, cancellationToken);
        return read.IsSuccess ? read.Value?.Registry ?? PublicationRegistry.Empty : Result.Failure<PublicationRegistry>(read.Error);
    }

    public async Task<Result<PublicationRegistry>> UpdateAsync(
        ShareId shareId, Func<PublicationRegistry, PublicationRegistry> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        for (int attempt = 1; attempt <= options.MaxCommitAttempts; attempt++)
        {
            Result<PublicationsSnapshot?> read = await store.GetPublicationsAsync(shareId, cancellationToken);
            if (read.IsFailure)
            {
                return Result.Failure<PublicationRegistry>(read.Error);
            }

            PublicationRegistry changed = change(read.Value?.Registry ?? PublicationRegistry.Empty);
            Result<string> written = await store.PutPublicationsAsync(shareId, changed, read.Value?.ETag, cancellationToken);
            if (written.IsSuccess)
            {
                return changed;
            }

            if (written.Error.Code != "Storage.PreconditionFailed")
            {
                return Result.Failure<PublicationRegistry>(written.Error);
            }

            if (attempt < options.MaxCommitAttempts)
            {
                await Task.Delay(committer.BackOff(attempt), timeProvider, cancellationToken);
            }
        }

        return Result.Failure<PublicationRegistry>(PublishErrors.RegistryRetriesExhausted(options.MaxCommitAttempts));
    }
}
