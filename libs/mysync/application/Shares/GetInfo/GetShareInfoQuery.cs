using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.GetInfo;

/// <summary>
/// <c>mysync info [&lt;name|shareId&gt;]</c> (CLAUDE.md §10): a share, its names, and how far this
/// folder is behind it. Without a reference, this folder's share; with one, any share (and this
/// folder's details only if it is that share). Reads only.
/// </summary>
public sealed record GetShareInfoQuery(ShareReference? Share = null) : IQuery<ShareInfoResponse>;

/// <param name="Names">Every name pointing at the share (none for a share from before names).</param>
/// <param name="RootPath">This folder, when it is the share; else <c>null</c>.</param>
/// <param name="BaseRevision">The revision this folder last agreed with; <c>null</c> when this folder isn't the share.</param>
/// <param name="RemoteRevision">HEAD now. Higher than <paramref name="BaseRevision"/> means another machine has synced since.</param>
/// <param name="FileCount">Files in HEAD.</param>
/// <param name="TotalBytes">Their total size.</param>
/// <param name="OpenConflicts">This folder's open conflicts; <c>null</c> when this folder isn't the share.</param>
public sealed record ShareInfoResponse(
    ShareId ShareId,
    IReadOnlyList<ShareName> Names,
    string? RootPath,
    StorageLocation Location,
    Revision? BaseRevision,
    Revision RemoteRevision,
    string RemoteUpdatedBy,
    DateTimeOffset RemoteUpdatedUtc,
    DateTimeOffset CreatedUtc,
    int FileCount,
    long TotalBytes,
    int? OpenConflicts,
    CompressionSetting Compression = CompressionSetting.None);

internal sealed class GetShareInfoQueryHandler(
    ShareFolder shareFolder, IShareStateStore stateStore, IShareStore store, ILocalFolder folder, ShareResolver resolver, ShareNames names)
    : IQueryHandler<GetShareInfoQuery, ShareInfoResponse>
{
    public async Task<Result<ShareInfoResponse>> Handle(GetShareInfoQuery query, CancellationToken cancellationToken)
    {
        ShareState? here;
        ShareId shareId;
        ShareName? typedName = null;
        StorageLocation location;
        if (query.Share is null)
        {
            Result<ShareState> loaded = await shareFolder.LoadAsync(cancellationToken);
            if (loaded.IsFailure)
            {
                return Result.Failure<ShareInfoResponse>(loaded.Error);
            }

            here = loaded.Value;
            shareId = here.ShareId;
            location = here.Location;
        }
        else
        {
            Result<StorageLocation> current = store.GetLocation();
            if (current.IsFailure)
            {
                return Result.Failure<ShareInfoResponse>(current.Error);
            }

            Result<ResolvedShare> resolved = await resolver.ResolveAsync(query.Share, cancellationToken);
            if (resolved.IsFailure)
            {
                return Result.Failure<ShareInfoResponse>(resolved.Error);
            }

            shareId = resolved.Value.ShareId;
            typedName = resolved.Value.Name;
            location = current.Value;

            // This folder's details only if it is that share, on this storage.
            Result<ShareState?> local = await stateStore.LoadAsync(cancellationToken);
            here = local is { IsSuccess: true, Value: { } state } && state.ShareId == shareId && state.CheckLocation(location).IsSuccess ? state : null;
        }

        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<ShareInfoResponse>(head.Error);
        }

        if (head.Value is not { Manifest: { } manifest })
        {
            return Result.Failure<ShareInfoResponse>(typedName is { } name ? ShareErrors.Incomplete(name, shareId) : ShareErrors.NotFound(shareId));
        }

        if (manifest.Deletion is { } deletion)
        {
            return Result.Failure<ShareInfoResponse>(ShareErrors.Deleted(shareId, deletion));
        }

        Result<IReadOnlyList<ShareName>> shareNames = await names.OfAsync(shareId, cancellationToken);
        if (shareNames.IsFailure)
        {
            return Result.Failure<ShareInfoResponse>(shareNames.Error);
        }

        return new ShareInfoResponse(
            shareId,
            shareNames.Value,
            here is null ? null : folder.RootPath,
            location,
            here?.BaseRevision,
            manifest.Revision,
            manifest.UpdatedBy,
            manifest.UpdatedUtc,
            manifest.CreatedUtc,
            manifest.FileCount,
            manifest.TotalSize,
            here?.Conflicts.Length,
            manifest.Compression);
    }
}
