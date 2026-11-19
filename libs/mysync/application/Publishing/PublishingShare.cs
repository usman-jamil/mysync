using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Publishing;

/// <summary>The share a publishing command acts on: <c>--share &lt;name|id&gt;</c>, or this folder's.</summary>
/// <param name="Here">This folder's state when this folder is that share, else <c>null</c>.</param>
internal sealed record PublishingTarget(ShareId ShareId, ShareState? Here);

internal sealed class PublishingShare(ShareFolder shareFolder, IShareStateStore stateStore, IShareStore store, ShareResolver resolver)
{
    public async Task<Result<PublishingTarget>> ResolveAsync(ShareReference? share, CancellationToken cancellationToken)
    {
        if (share is null)
        {
            Result<ShareState> folderState = await shareFolder.LoadAsync(cancellationToken);
            return folderState.IsSuccess ? new PublishingTarget(folderState.Value.ShareId, folderState.Value) : Result.Failure<PublishingTarget>(folderState.Error);
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<PublishingTarget>(location.Error);
        }

        Result<ResolvedShare> resolved = await resolver.ResolveAsync(share, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<PublishingTarget>(resolved.Error);
        }

        ShareId id = resolved.Value.ShareId;

        // From another folder this works too; a damaged state file here only means it isn't used.
        Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
        ShareState? here = loaded is { IsSuccess: true, Value: { } state } && state.ShareId == id && state.CheckLocation(location.Value).IsSuccess ? state : null;
        return new PublishingTarget(id, here);
    }
}
