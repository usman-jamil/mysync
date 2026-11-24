using MySync.Application.Abstractions.Publishing;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Cli.Hosting;

/// <summary>
/// The publish target when <c>MYSYNC_PUBLISH_PROVIDER</c> names no provider mysync has: every
/// command that resolves it still starts (so <c>delete</c> of a share without publications works),
/// and the one that needs it reports the configuration error (exit 2).
/// </summary>
internal sealed class UnconfiguredPublishTarget(Error error) : IPublishTarget
{
    public Result<PublishLocation> GetLocation() => Result.Failure<PublishLocation>(error);

    public Task<Result> PreflightAsync(CancellationToken cancellationToken) => Task.FromResult(Result.Failure(error));

    public bool SharesAccountWith(StorageLocation shares) => false;

    public bool CanCopy(CopySource source) => false;

    public Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<bool>(error));

    public Task<Result<CopyMethod>> CopyAsync(CopySource source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<CopyMethod>(error));

    public Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(error));

    public Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken) => Task.FromResult(Result.Failure<int>(error));
}
