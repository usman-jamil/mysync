using System.Collections.Immutable;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;

namespace MySync.Application.Session;

/// <summary>What a file (or empty folder) of the share folder is, as the next sync sees it (CLAUDE.md §10d).</summary>
public enum FileStatus
{
    /// <summary>The same here and in the share: sync does nothing.</summary>
    Synced = 0,

    /// <summary>Added in this folder: sync uploads it.</summary>
    NewHere = 1,

    /// <summary>Modified in this folder: sync uploads it.</summary>
    ChangedHere = 2,

    /// <summary>Removed from this folder: sync removes it from the share.</summary>
    DeletedHere = 3,

    /// <summary>Added by another machine: sync downloads it.</summary>
    NewInShare = 4,

    /// <summary>Modified by another machine: sync downloads it.</summary>
    ChangedInShare = 5,

    /// <summary>Removed by another machine: sync deletes it here.</summary>
    DeletedInShare = 6,

    /// <summary>Both sides changed it: sync leaves it; it needs a decision.</summary>
    Conflict = 7,

    /// <summary>Matched by the ignore rules: sync does nothing.</summary>
    Ignored = 8,

    /// <summary>Can't be handled now (busy, unreadable, a name this disk can't hold, …), with the reason.</summary>
    Skipped = 9,
}

/// <summary>One path of the status listing.</summary>
/// <param name="Path">Relative to the share root, <c>/</c>-separated (a skipped local path may not be a valid share path).</param>
/// <param name="IsFolder">An empty folder (<c>dir</c> entry); folders with files are implied by their files' paths.</param>
/// <param name="Size">The size on the side that matters (this folder's for local changes, the share's for share changes).</param>
/// <param name="MtimeUtc">Its modified time, on the same side.</param>
/// <param name="ChangedBy">Share-side changes and conflicts: who committed the share's version (HEAD's author, or the one a conflict recorded).</param>
/// <param name="ChangedAt">The revision that version is from.</param>
/// <param name="Detail">Skipped and ignored paths: why.</param>
public sealed record FolderEntry(
    string Path,
    bool IsFolder,
    FileStatus Status,
    long? Size,
    DateTimeOffset? MtimeUtc,
    string? ChangedBy = null,
    Revision? ChangedAt = null,
    ConflictKind? ConflictKind = null,
    string? Detail = null)
{
    /// <summary>The status says sync uploads it.</summary>
    public bool IsUpload => Status is FileStatus.NewHere or FileStatus.ChangedHere;

    /// <summary>The status says sync downloads it.</summary>
    public bool IsDownload => Status is FileStatus.NewInShare or FileStatus.ChangedInShare;

    /// <summary>The status says sync deletes it on one side.</summary>
    public bool IsDelete => Status is FileStatus.DeletedHere or FileStatus.DeletedInShare;

    /// <summary>It is on disk in this folder now (it can be pushed).</summary>
    public bool ExistsHere => Status is not (FileStatus.NewInShare or FileStatus.DeletedHere);
}

/// <summary>The count and size of one kind of pending change.</summary>
public sealed record PendingTotals(int Files, long Bytes)
{
    public static PendingTotals None { get; } = new(0, 0);
}

/// <summary>
/// The status of the share folder at one moment: every path the merge plan covers, plus the
/// skipped and ignored ones, and the share's HEAD it was compared with. Built from a preview
/// (<c>mysync status</c>'s <see cref="SyncReport"/>), so the merge rules are never repeated here.
/// </summary>
/// <param name="HashCache">The hashes this scan used and computed: the session keeps them in memory for the next rescan.</param>
/// <param name="ShareEntryCount">Entries in HEAD: 0 for a new, empty share.</param>
/// <param name="RecordedConflicts">Conflicts a sync has recorded in <c>.mysynctool</c>: only those can be resolved (a rescan finds new ones but writes nothing).</param>
public sealed record FolderStatus(
    ShareId ShareId,
    IReadOnlyList<ShareName> Names,
    StorageLocation Location,
    string RootPath,
    Revision HeadRevision,
    string? HeadUpdatedBy,
    DateTimeOffset? HeadUpdatedUtc,
    CompressionSetting Compression,
    int ShareEntryCount,
    ImmutableArray<FolderEntry> Entries,
    ImmutableArray<Conflict> Conflicts,
    ImmutableArray<string> Warnings,
    ImmutableSortedDictionary<SharePath, HashCacheEntry> HashCache,
    IReadOnlyCollection<string>? RecordedConflicts = null)
{
    /// <summary>Whether a sync has recorded this conflict, so <c>resolve</c> can decide it.</summary>
    public bool IsRecorded(string path) => RecordedConflicts?.Contains(path) == true;

    /// <summary>The share's name if it has one, else its ID: how messages name it.</summary>
    public string ShareLabel => Names.Count > 0 ? Names[0].Value : ShareId.ToString();

    public PendingTotals ToUpload => Totals(entry => entry.IsUpload);

    public PendingTotals ToDownload => Totals(entry => entry.IsDownload);

    /// <summary>Deletes on either side.</summary>
    public int ToDelete => Entries.Count(entry => entry.IsDelete);

    public int ConflictCount => Entries.Count(entry => entry.Status == FileStatus.Conflict);

    public int SkippedCount => Entries.Count(entry => entry.Status == FileStatus.Skipped);

    /// <summary>Changes a sync would make (uploads, downloads, deletes).</summary>
    public int PendingChanges => Entries.Count(entry => entry.IsUpload || entry.IsDownload || entry.IsDelete);

    public bool IsShareEmpty => ShareEntryCount == 0;

    private PendingTotals Totals(Func<FolderEntry, bool> select)
    {
        List<FolderEntry> matching = [.. Entries.Where(select)];
        return new PendingTotals(matching.Count, matching.Sum(entry => entry.Size ?? 0));
    }
}

/// <summary>The one place a merge plan becomes file statuses. Every <see cref="SyncActionKind"/> maps to exactly one <see cref="FileStatus"/>.</summary>
internal static class FolderStatusMapper
{
    public static ImmutableArray<FolderEntry> Map(SyncReport preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var skipped = preview.Skipped.ToDictionary(skip => skip.Path, StringComparer.Ordinal);
        var conflicts = preview.Conflicts.ToDictionary(conflict => conflict.Path);
        List<FolderEntry> entries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SyncAction action in preview.Plan.Actions)
        {
            entries.Add(Entry(action, preview, skipped, conflicts));
            seen.Add(action.Path.Value);
        }

        // Paths the scan skipped that the plan doesn't hold (names no share path can hold).
        foreach (SkippedPath skip in preview.Skipped.Where(skip => !seen.Contains(skip.Path)))
        {
            entries.Add(new FolderEntry(skip.Path, IsFolder: false, Status(skip), Size: null, MtimeUtc: null, Detail: skip.Detail));
        }

        return [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];
    }

    /// <summary>The status a plan action means. Public for the exhaustive test.</summary>
    public static FileStatus StatusOf(SyncAction action, SkippedPath? skip)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action.Kind switch
        {
            SyncActionKind.NoChange => FileStatus.Synced,
            SyncActionKind.Upload or SyncActionKind.UpdateRemoteMode or SyncActionKind.AddRemoteDirectory =>
                action.Base is null || action.Remote is null ? FileStatus.NewHere : FileStatus.ChangedHere,
            SyncActionKind.RemoveRemote => FileStatus.DeletedHere,
            SyncActionKind.Download or SyncActionKind.UpdateLocalMode or SyncActionKind.CreateLocalDirectory =>
                action.Base is null || action.Local is null ? FileStatus.NewInShare : FileStatus.ChangedInShare,
            SyncActionKind.DeleteLocal => FileStatus.DeletedInShare,
            SyncActionKind.Conflict => FileStatus.Conflict,
            SyncActionKind.Skip => skip is not null ? Status(skip) : FileStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "An action kind with no status."),
        };
    }

    private static FolderEntry Entry(SyncAction action, SyncReport preview, Dictionary<string, SkippedPath> skipped, Dictionary<SharePath, Conflict> conflicts)
    {
        SkippedPath? skip = skipped.GetValueOrDefault(action.Path.Value);
        FileStatus status = StatusOf(action, skip);

        // Which side's size and time to show: the one the change comes from.
        ManifestEntry? shown = status switch
        {
            FileStatus.NewInShare or FileStatus.ChangedInShare => action.Remote,
            FileStatus.DeletedHere => action.Base ?? action.Remote,
            _ => action.Local ?? action.Remote ?? action.Base,
        };
        var file = shown as FileEntry;
        bool isFolder = shown is DirectoryEntry;

        string? changedBy = null;
        Revision? changedAt = null;
        if (status == FileStatus.Conflict && conflicts.GetValueOrDefault(action.Path) is { } conflict)
        {
            changedBy = conflict.RemoteUpdatedBy;
            changedAt = conflict.RemoteRevision;
        }
        else if (status is FileStatus.NewInShare or FileStatus.ChangedInShare or FileStatus.DeletedInShare)
        {
            changedBy = preview.HeadUpdatedBy;
            changedAt = preview.Revision;
        }

        string? detail = status switch
        {
            FileStatus.Skipped or FileStatus.Ignored => skip?.Detail ?? (action.SkipReason == SkipReason.NoDelete ? "kept by --no-delete" : "left for the next sync"),
            _ => null,
        };

        return new FolderEntry(action.Path.Value, isFolder, status, file?.Size, file?.MtimeUtc, changedBy, changedAt, action.ConflictKind, detail);
    }

    private static FileStatus Status(SkippedPath skip) => skip.Cause == SkipCause.IgnoredLocally ? FileStatus.Ignored : FileStatus.Skipped;
}
