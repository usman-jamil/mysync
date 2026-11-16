using SharedKernel;

namespace MySync.Domain.Sync;

/// <summary>
/// Stops a sync that would delete most of either side (CLAUDE.md §9 phase 5): an unmounted
/// drive, a wrong folder or a damaged scan makes everything look deleted.
/// </summary>
/// <remarks>
/// Blocks when a plan deletes more than half <b>and</b> more than <see cref="MinimumDeletes"/>
/// of the local files, or of the remote entries. Small folders never trip it: deleting 10 of 10
/// files is allowed. <c>--allow-mass-delete</c> overrides it.
/// </remarks>
public static class MassDeleteGuard
{
    public const int MinimumDeletes = 10;

    public static Result Check(SyncPlan plan, bool allowMassDelete)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (allowMassDelete)
        {
            return Result.Success();
        }

        if (IsMassDelete(plan.LocalFileDeletes, plan.LocalFileCount))
        {
            return Result.Failure(SyncErrors.MassDeleteBlocked(plan.LocalFileDeletes, plan.LocalFileCount));
        }

        if (IsMassDelete(plan.RemoteEntryDeletes, plan.RemoteEntryCount))
        {
            return Result.Failure(SyncErrors.MassDeleteBlocked(plan.RemoteEntryDeletes, plan.RemoteEntryCount));
        }

        return Result.Success();
    }

    private static bool IsMassDelete(int deletes, int total) => deletes > MinimumDeletes && deletes * 2L > total;
}
