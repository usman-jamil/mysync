using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares;

/// <summary>
/// "This folder is a share, and the environment points at its storage": what <c>status</c>,
/// <c>info</c>, <c>resolve</c> and <c>gc</c> check before anything else. A missing state file is
/// <see cref="ShareErrors.NotAShare"/>; a damaged one is <c>State.Corrupt</c>; a different account
/// or container is <c>State.StorageMismatch</c>.
/// </summary>
internal sealed class ShareFolder(IShareStateStore stateStore, IShareStore store)
{
    public async Task<Result<ShareState>> LoadAsync(CancellationToken cancellationToken)
    {
        Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ShareState>(loaded.Error);
        }

        if (loaded.Value is not { } state)
        {
            return Result.Failure<ShareState>(ShareErrors.NotAShare());
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<ShareState>(location.Error);
        }

        Result sameStorage = state.CheckLocation(location.Value);
        return sameStorage.IsSuccess ? state : Result.Failure<ShareState>(sameStorage.Error);
    }
}
