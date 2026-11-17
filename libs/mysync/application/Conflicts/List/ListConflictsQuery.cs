using System.Collections.Immutable;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Conflicts.List;

/// <summary>
/// <c>mysync conflicts</c> (CLAUDE.md §10): the open conflicts recorded by the last sync, from
/// <c>.mysynctool</c> alone. No network, no lock: it works offline and while a sync is running.
/// </summary>
public sealed record ListConflictsQuery : IQuery<ConflictListResponse>;

public sealed record ConflictListResponse(ShareId ShareId, ImmutableArray<Conflict> Conflicts);

internal sealed class ListConflictsQueryHandler(IShareStateStore stateStore) : IQueryHandler<ListConflictsQuery, ConflictListResponse>
{
    public async Task<Result<ConflictListResponse>> Handle(ListConflictsQuery query, CancellationToken cancellationToken)
    {
        Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ConflictListResponse>(loaded.Error);
        }

        return loaded.Value is { } state
            ? new ConflictListResponse(state.ShareId, state.Conflicts)
            : Result.Failure<ConflictListResponse>(ShareErrors.NotAShare());
    }
}
