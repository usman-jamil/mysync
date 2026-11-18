using MySync.Domain.Paths;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Abstractions.Logging;

/// <summary>
/// <c>.mysynctool.log</c> (CLAUDE.md §7): one human-readable line per conflict detected or
/// resolved. The pipeline calls <see cref="RecordDetectedAsync"/> only when a conflict is new or
/// its hashes changed. A failure here is a warning, never a reason to fail the sync.
/// </summary>
public interface IConflictLog
{
    Task<Result> RecordDetectedAsync(Conflict conflict, CancellationToken cancellationToken);

    Task<Result> RecordResolvedAsync(SharePath path, KeepSide kept, DateTimeOffset resolvedUtc, CancellationToken cancellationToken);

    /// <summary>Removes the log and its rotated copy: it lists file paths, which a deleted share may not want left behind. Missing = success.</summary>
    Task<Result> DeleteAsync(CancellationToken cancellationToken);
}
