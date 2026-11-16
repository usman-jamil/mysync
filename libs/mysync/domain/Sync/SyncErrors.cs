using SharedKernel;

namespace MySync.Domain.Sync;

public static class SyncErrors
{
    public static Error MassDeleteBlocked(int deleteCount, int total) => Error.Problem(
        "Sync.MassDeleteBlocked",
        $"This sync would delete {deleteCount} of {total} entries. Check you are in the right folder, then re-run with --allow-mass-delete.");

    public static Error CommitRetriesExhausted(int attempts) => Error.Conflict(
        "Sync.CommitRetriesExhausted",
        $"Other machines kept committing to this share; gave up after {attempts} attempts. Run 'mysync sync' again.");
}
