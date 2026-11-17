using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.GetInfo;

/// <summary><c>mysync info</c> (CLAUDE.md §10): this folder's share and how far behind the remote it is. Reads only.</summary>
public sealed record GetShareInfoQuery : IQuery<ShareInfoResponse>;

/// <param name="BaseRevision">The revision this folder last agreed with.</param>
/// <param name="RemoteRevision">HEAD now. Higher than <paramref name="BaseRevision"/> means another machine has synced since.</param>
/// <param name="FileCount">Files in HEAD.</param>
/// <param name="TotalBytes">Their total size.</param>
public sealed record ShareInfoResponse(
    ShareId ShareId,
    string RootPath,
    StorageLocation Location,
    Revision BaseRevision,
    Revision RemoteRevision,
    string RemoteUpdatedBy,
    DateTimeOffset RemoteUpdatedUtc,
    DateTimeOffset CreatedUtc,
    int FileCount,
    long TotalBytes,
    int OpenConflicts);

internal sealed class GetShareInfoQueryHandler(ShareFolder shareFolder, IShareStore store, ILocalFolder folder)
    : IQueryHandler<GetShareInfoQuery, ShareInfoResponse>
{
    public async Task<Result<ShareInfoResponse>> Handle(GetShareInfoQuery query, CancellationToken cancellationToken)
    {
        Result<ShareState> loaded = await shareFolder.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ShareInfoResponse>(loaded.Error);
        }

        ShareState state = loaded.Value;
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(state.ShareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<ShareInfoResponse>(head.Error);
        }

        if (head.Value is not { Manifest: { } manifest })
        {
            return Result.Failure<ShareInfoResponse>(ShareErrors.NotFound(state.ShareId));
        }

        return new ShareInfoResponse(
            state.ShareId,
            folder.RootPath,
            state.Location,
            state.BaseRevision,
            manifest.Revision,
            manifest.UpdatedBy,
            manifest.UpdatedUtc,
            manifest.CreatedUtc,
            manifest.FileCount,
            manifest.TotalSize,
            state.Conflicts.Length);
    }
}
