using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Publishing;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Shares.Compression;

/// <summary>
/// <c>mysync compression on|off</c> (CLAUDE.md §6): commits a new revision with the setting
/// changed, through the usual <c>If-Match</c> and retry. No object is touched: the setting governs
/// only files uploaded from then on. Already in that state → success, no new revision.
/// </summary>
/// <param name="Share">A share's name or ID; <c>null</c> for this folder's share.</param>
public sealed record SetCompressionCommand(bool On, ShareReference? Share = null) : ICommand<CompressionResponse>;

/// <summary><c>mysync compression</c>: the share's current setting, from HEAD.</summary>
public sealed record GetCompressionQuery(ShareReference? Share = null) : IQuery<CompressionResponse>;

/// <param name="Revision">HEAD after the command: the new revision, or the current one when nothing changed.</param>
/// <param name="Changed">False when the share was already in the requested state.</param>
public sealed record CompressionResponse(ShareId ShareId, CompressionSetting Setting, Revision Revision, int FormatVersion, bool Changed = false);

internal sealed class SetCompressionCommandHandler(
    PublishingShare share,
    RemoteCommitter committer,
    IMachineIdentity machine,
    SharedKernel.IDateTimeProvider clock)
    : ICommandHandler<SetCompressionCommand, CompressionResponse>
{
    public async Task<Result<CompressionResponse>> Handle(SetCompressionCommand command, CancellationToken cancellationToken)
    {
        Result<PublishingTarget> target = await share.ResolveAsync(command.Share, cancellationToken);
        if (target.IsFailure)
        {
            return Result.Failure<CompressionResponse>(target.Error);
        }

        CompressionSetting wanted = command.On ? CompressionSetting.Gzip : CompressionSetting.None;
        Result<CommitOutcome> commit = await committer.CommitAsync(
            target.Value.ShareId,
            (snapshot, _) =>
            {
                if (snapshot.Manifest.Compression == wanted)
                {
                    return Task.FromResult(Result.Success<Manifest?>(null));
                }

                Result<Manifest> next = snapshot.Manifest.WithCompression(wanted, new DateTimeOffset(clock.UtcNow, TimeSpan.Zero), machine.Name);
                return Task.FromResult(next.IsSuccess ? Result.Success<Manifest?>(next.Value) : Result.Failure<Manifest?>(next.Error));
            },
            cancellationToken);
        if (commit.IsFailure)
        {
            return Result.Failure<CompressionResponse>(commit.Error);
        }

        Manifest head = commit.Value.Committed ?? commit.Value.Remote.Manifest;
        return new CompressionResponse(target.Value.ShareId, head.Compression, head.Revision, head.FormatVersion, commit.Value.Committed is not null);
    }
}

internal sealed class GetCompressionQueryHandler(PublishingShare share, IShareStore store) : IQueryHandler<GetCompressionQuery, CompressionResponse>
{
    public async Task<Result<CompressionResponse>> Handle(GetCompressionQuery query, CancellationToken cancellationToken)
    {
        Result<PublishingTarget> target = await share.ResolveAsync(query.Share, cancellationToken);
        if (target.IsFailure)
        {
            return Result.Failure<CompressionResponse>(target.Error);
        }

        ShareId shareId = target.Value.ShareId;
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<CompressionResponse>(head.Error);
        }

        if (head.Value is not { Manifest: { } manifest })
        {
            return Result.Failure<CompressionResponse>(ShareErrors.NotFound(shareId));
        }

        return manifest.Deletion is { } deletion
            ? Result.Failure<CompressionResponse>(ShareErrors.Deleted(shareId, deletion))
            : new CompressionResponse(shareId, manifest.Compression, manifest.Revision, manifest.FormatVersion);
    }
}
