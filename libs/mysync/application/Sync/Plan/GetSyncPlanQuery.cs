using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Shares;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Sync.Plan;

/// <summary>
/// <c>mysync status</c> (CLAUDE.md §10): the plan <c>sync</c> would carry out now, with conflicts
/// and warnings. A query, so it writes nothing: no lock, no state (not even the hash cache), no
/// log, no staging clean-up. It can run while a sync is in progress.
/// </summary>
public sealed record GetSyncPlanQuery : IQuery<SyncReport>;

internal sealed class GetSyncPlanQueryHandler(ShareFolder shareFolder, SyncPipeline pipeline, SyncOptions options)
    : IQueryHandler<GetSyncPlanQuery, SyncReport>
{
    public async Task<Result<SyncReport>> Handle(GetSyncPlanQuery query, CancellationToken cancellationToken)
    {
        Result<ShareState> state = await shareFolder.LoadAsync(cancellationToken);
        if (state.IsFailure)
        {
            return Result.Failure<SyncReport>(state.Error);
        }

        return await pipeline.PreviewAsync(
            new SyncSession(
                state.Value.ShareId,
                state.Value.Location,
                state.Value.Base,
                state.Value.HashCache,
                state.Value.Conflicts,
                MergeOptions.Default,
                AllowMassDelete: false,
                DryRun: true,
                NullSyncProgressReporter.Instance,
                options.MaxParallelism),
            cancellationToken);
    }
}
