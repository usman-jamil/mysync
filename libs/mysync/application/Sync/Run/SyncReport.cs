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

    /// <summary><c>status</c> and <c>mysync tui</c> only: the hashes the scan used and computed, for a caller that keeps them in memory (nothing is written).</summary>
    public ImmutableSortedDictionary<SharePath, Domain.State.HashCacheEntry>? ScanHashCache { get; init; }

    /// <summary>Preview only: who committed HEAD, and when.</summary>
    public string? HeadUpdatedBy { get; init; }

    public DateTimeOffset? HeadUpdatedUtc { get; init; }

    /// <summary>Preview only: HEAD's compression setting.</summary>
    public CompressionSetting HeadCompression { get; init; }

    /// <summary>Preview only: how many entries HEAD has (0: a new, empty share).</summary>
    public int HeadEntryCount { get; init; }
}

/// <param name="Bytes">The files' own (uncompressed) size.</param>
/// <param name="StoredBytes">What actually went over the wire: less than <paramref name="Bytes"/> when files were sent compressed.</param>
public sealed record TransferTotals(int Files, long Bytes, long StoredBytes)
{
    public TransferTotals(int files, long bytes)
        : this(files, bytes, bytes)
    {
    }

    public static TransferTotals None { get; } = new(0, 0);

    /// <summary>True when something was sent compressed.</summary>
    public bool Compressed => StoredBytes < Bytes;

    public TransferTotals Plus(TransferTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TransferTotals(Files + other.Files, Bytes + other.Bytes, StoredBytes + other.StoredBytes);
    }
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

/// <summary>The kebab-case code of a <see cref="SkipCause"/>, as in <c>--json</c> output and the push record.</summary>
public static class SkipCauseCodes
{
    public static string Of(SkipCause cause) => cause switch
    {
        SkipCause.Unstable => "unstable",
        SkipCause.Unreadable => "unreadable",
        SkipCause.InvalidName => "invalid-name",
        SkipCause.Symlink => "symlink",
        SkipCause.NoDelete => "no-delete",
        SkipCause.ChangedSinceScan => "changed-since-scan",
        SkipCause.ApplyFailed => "apply-failed",
        SkipCause.CaseCollision => "case-collision",
        SkipCause.WindowsInvalidName => "windows-invalid-name",
        SkipCause.IgnoredLocally => "ignored-locally",
        _ => cause.ToString(),
    };
}
