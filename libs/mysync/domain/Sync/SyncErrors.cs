using SharedKernel;

namespace MySync.Domain.Sync;

public static class SyncErrors
{
    public static Error MassDeleteBlocked(int deleteCount, int total) => Error.Problem(
        "Sync.MassDeleteBlocked",
        $"This sync would delete {deleteCount} of {total} entries. Check you are in the right folder, then re-run with --allow-mass-delete.");

    /// <summary><c>resolve</c> was given paths that have no open conflict. A usage error: nothing was done.</summary>
    public static Error NotInConflict(IReadOnlyCollection<string> paths) => Error.Validation(
        "Sync.NotInConflict",
        $"Not in conflict: {string.Join(", ", paths.Select(Paths.PathErrors.Display))}. 'mysync conflicts' lists the open ones.");

    /// <summary><c>gc</c> saw the share change between planning and deleting, so it deleted nothing.</summary>
    public static Error ShareChangedDuringGc(long plannedRevision, long currentRevision) => Error.Conflict(
        "Sync.ShareChangedDuringGc",
        $"The share moved from revision {plannedRevision} to {currentRevision} while gc was planning; nothing was deleted. Run 'mysync gc' again when no one is syncing.");

    public static Error CommitRetriesExhausted(int attempts) => Error.Conflict(
        "Sync.CommitRetriesExhausted",
        $"Other machines kept committing to this share; gave up after {attempts} attempts. Run 'mysync sync' again.");
}
