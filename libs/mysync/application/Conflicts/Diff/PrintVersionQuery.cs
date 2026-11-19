using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Shares;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Conflicts.Diff;

/// <summary>
/// <c>mysync diff &lt;path&gt; --print local|remote|base</c>: one version's raw bytes, for the user's own
/// merge tool. Remote and base are the conflict's recorded versions (else HEAD's entry and this
/// folder's base). Streams without a limit; a stored version's hash is checked as it ends.
/// <c>local</c> needs no storage. The caller disposes the stream. Nothing is written.
/// </summary>
public sealed record PrintVersionQuery(string Path, DiffSideKind Side) : IQuery<Stream>;

internal sealed class PrintVersionQueryHandler(IShareStateStore stateStore, ShareFolder shareFolder, ILocalFolder folder, DiffSources sources)
    : IQueryHandler<PrintVersionQuery, Stream>
{
    public async Task<Result<Stream>> Handle(PrintVersionQuery query, CancellationToken cancellationToken)
    {
        Result<SharePath> path = SharePath.Create(query.Path);
        if (path.IsFailure)
        {
            return Result.Failure<Stream>(path.Error);
        }

        if (query.Side == DiffSideKind.Local)
        {
            Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
            if (loaded.IsFailure)
            {
                return Result.Failure<Stream>(loaded.Error);
            }

            if (loaded.Value is null)
            {
                return Result.Failure<Stream>(ShareErrors.NotAShare());
            }

            Result<LocalFile?> file = await folder.GetFileAsync(path.Value, cancellationToken);
            if (file.IsFailure)
            {
                return Result.Failure<Stream>(file.Error);
            }

            return file.Value is null
                ? Result.Failure<Stream>(DiffErrors.VersionMissing(path.Value.Value, "local"))
                : await folder.OpenReadAsync(path.Value, cancellationToken);
        }

        Result<ShareState> share = await shareFolder.LoadAsync(cancellationToken);
        if (share.IsFailure)
        {
            return Result.Failure<Stream>(share.Error);
        }

        ShareState state = share.Value;
        Conflict? conflict = state.Conflicts.FirstOrDefault(open => open.Path == path.Value);
        ManifestEntry? entry;
        if (query.Side == DiffSideKind.Base)
        {
            entry = conflict is not null ? conflict.Base : state.Base.GetValueOrDefault(path.Value);
        }
        else if (conflict is not null)
        {
            entry = conflict.Remote;
        }
        else
        {
            Result<Manifest> head = await sources.HeadAsync(state.ShareId, cancellationToken);
            if (head.IsFailure)
            {
                return Result.Failure<Stream>(head.Error);
            }

            entry = head.Value.Entries.GetValueOrDefault(path.Value);
        }

        return entry is FileEntry stored
            ? await sources.OpenStoredAsync(state.ShareId, path.Value, query.Side, stored, cancellationToken)
            : Result.Failure<Stream>(DiffErrors.VersionMissing(path.Value.Value, DiffQueryHandler.Name(query.Side)));
    }
}
