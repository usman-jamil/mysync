using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;

namespace MySync.Application.Sync.Run;

/// <summary>
/// The outcome of a sync, clone or resolve. Partial outcomes are successes (CLAUDE.md §3): the
/// CLI turns open <see cref="Conflicts"/> into exit 3 and <see cref="Skipped"/> into exit 9.
/// </summary>
/// <param name="Revision">The revision this folder is now based on.</param>
/// <param name="Committed">True if this run committed a new revision.</param>
/// <param name="Plan">What was planned (for <c>--dry-run</c> and the summary), with deferred paths marked.</param>
public sealed record SyncReport(
    ShareId ShareId,
    Revision Revision,
    bool Committed,
    bool DryRun,
    TransferTotals Uploaded,
    TransferTotals Downloaded,
    int DeletedLocally,
    int DeletedRemotely,
    ImmutableArray<Conflict> Conflicts,
    ImmutableArray<SkippedPath> Skipped,
    ImmutableArray<string> Warnings,
    SyncPlan Plan)
{
    /// <summary><c>resolve</c> only: the named conflicts it settled. The ones still open are in <see cref="Conflicts"/>.</summary>
    public ImmutableArray<SharePath> Resolved { get; init; } = [];

    /// <summary>Clone by name: the name it was cloned by.</summary>
    public ShareName? Name { get; init; }
}

public sealed record TransferTotals(int Files, long Bytes)
{
    public static TransferTotals None { get; } = new(0, 0);
}

public enum SkipCause
{
    /// <summary>Changed while being hashed or uploaded.</summary>
    Unstable = 0,

    /// <summary>Locked or unreadable.</summary>
    Unreadable = 1,

    /// <summary>A name no share path can hold.</summary>
    InvalidName = 2,

    /// <summary>Symlink or junction (not synced in v1).</summary>
    Symlink = 3,

    /// <summary>Kept because of <c>--no-delete</c>.</summary>
    NoDelete = 4,

    /// <summary>The local file changed after the scan; picked up next run.</summary>
    ChangedSinceScan = 5,

    /// <summary>Reading, writing or downloading it failed.</summary>
    ApplyFailed = 6,

    /// <summary>Differs from another entry only in case, on a case-insensitive filesystem.</summary>
    CaseCollision = 7,

    /// <summary>A name Windows can't hold.</summary>
    WindowsInvalidName = 8,

    /// <summary>The remote has it, but the local ignore rules match it.</summary>
    IgnoredLocally = 9,
}

/// <param name="Path">Display with <see cref="Domain.Paths.PathErrors.Display"/>: it may not be a valid share path.</param>
public sealed record SkippedPath(string Path, SkipCause Cause, string Detail);
