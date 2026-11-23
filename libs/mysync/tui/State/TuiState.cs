using System.Collections.Immutable;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Session;
using MySync.Tui.Rendering;
using SharedKernel;

namespace MySync.Tui.State;

/// <summary>Which screen is showing (a running sync, resolve or push shows the progress view on top of Main).</summary>
public enum Screen
{
    Main = 0,
    Conflicts = 1,
    Diff = 2,
}

/// <summary>The list filters, in the order <c>f</c> cycles through them and <c>1</c>–<c>6</c> pick them.</summary>
public enum ListFilter
{
    All = 0,
    Here = 1,
    Share = 2,
    Conflicts = 3,
    Other = 4,
    Synced = 5,
}

/// <summary>What the command decided before the screen opened.</summary>
/// <param name="FolderPath">The folder's full path, for the header.</param>
/// <param name="HostName">This machine's host folder name, for the push preview.</param>
/// <param name="MachineName"><c>user@MACHINE</c>, for "yours" in the conflict comparison.</param>
/// <param name="LargeFileCount">A first upload above this many files asks first (the CLI's threshold).</param>
/// <param name="LargeTotalBytes">… or above this many bytes.</param>
/// <param name="StartNotes">Lines for the activity list at start (ignored options, a new share).</param>
public sealed record TuiOptions(
    bool Ascii,
    ColorDepth Colors,
    string FolderPath,
    string HostName,
    string MachineName = "",
    int LargeFileCount = 1000,
    long LargeTotalBytes = 1L << 30,
    ImmutableArray<string> StartNotes = default)
{
    public Symbols Symbols => Ascii ? Symbols.Ascii : Symbols.Unicode;
}

/// <summary>One visible row of the main list: a folder or a file, at its depth.</summary>
/// <param name="Changes">A folder: pending changes below it.</param>
public sealed record ListRow(
    string Path,
    string Name,
    int Depth,
    bool IsFolder,
    bool Expanded,
    FolderEntry? Entry,
    int Changes = 0,
    int Conflicts = 0,
    int Skipped = 0,
    int Files = 0,
    long Bytes = 0);

public sealed record ActivityLine(DateTimeOffset Time, string Text, Role Role);

/// <summary>
/// What the views need from a status, worked out once per rescan (never per frame, so drawing
/// doesn't grow with the folder): the summary row's totals, the filter counts, the conflicts.
/// </summary>
public sealed record StatusSummary(
    PendingTotals ToUpload,
    PendingTotals ToDownload,
    int ToDelete,
    int ConflictCount,
    int SkippedCount,
    int PendingChanges,
    int Total,
    IReadOnlyDictionary<ListFilter, int> FilterCounts,
    ImmutableArray<FolderEntry> Conflicts)
{
    public static StatusSummary Empty { get; } = new(
        PendingTotals.None, PendingTotals.None, 0, 0, 0, 0, 0, Enum.GetValues<ListFilter>().ToDictionary(filter => filter, _ => 0), []);

    public static StatusSummary Of(FolderStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new StatusSummary(
            status.ToUpload,
            status.ToDownload,
            status.ToDelete,
            status.ConflictCount,
            status.SkippedCount,
            status.PendingChanges,
            status.Entries.Length,
            ListModel.Counts(status),
            [.. status.Entries.Where(entry => entry.Status == FileStatus.Conflict)]);
    }
}

/// <summary>The one-line message under the filters.</summary>
public sealed record Banner(string Text, Role Role);

public enum ItemState
{
    Waiting = 0,
    Active = 1,
    Done = 2,
    Conflict = 3,
}

/// <summary>One item of a running job, for its list.</summary>
/// <param name="Action">What happens to it: <c>upload</c>, <c>download</c>, <c>delete here</c>, <c>delete in share</c>, <c>conflict</c>, <c>push</c>.</param>
public sealed record JobItem(string Path, TransferDirection? Direction, string Action, long Size, ItemState State);

/// <param name="Current">The file transferring now (the last one started).</param>
public sealed record DirectionProgress(int Files, long Bytes, int FilesDone, long BytesDone, long Rate, string? Current)
{
    public static DirectionProgress Planned(int files, long bytes) => new(files, bytes, 0, 0, 0, null);

    public int Percent
    {
        get
        {
            if (Bytes > 0)
            {
                return (int)Math.Clamp(BytesDone * 100 / Bytes, 0, 100);
            }

            return Files > 0 ? FilesDone * 100 / Files : 0;
        }
    }
}

/// <summary>A running sync, resolve or push: what the progress view shows.</summary>
public sealed record JobView(
    SessionJob Job,
    string Title,
    SyncPhase? Phase,
    DirectionProgress? Upload,
    DirectionProgress? Download,
    ImmutableArray<JobItem> Items,
    bool Cancelling = false);

public abstract record Dialog;

public enum PushField
{
    Share = 0,
    Create = 1,
}

/// <param name="ItemPath">Relative to the folder; a folder ends without <c>/</c>.</param>
/// <param name="Error">Why Enter does nothing now; <c>null</c> when the name is fine (or empty).</param>
public sealed record PushDialog(string ItemPath, bool ItemIsFolder, int Files, long Bytes, string Share, bool Create, PushField Focus, string? Error) : Dialog;

public enum ConfirmKind
{
    MassDelete = 0,
    LargeUpload = 1,
    QuitDuringJob = 2,
}

public sealed record ConfirmDialog(ConfirmKind Kind, string Title, ImmutableArray<string> Lines, string YesLabel) : Dialog;

public sealed record HelpDialog : Dialog;

public sealed record DiffLineView(string Text, Role Role);

/// <summary>The diff view of one conflicted path.</summary>
public sealed record DiffState(string Path, bool Loading, ImmutableArray<DiffLineView> Lines, int Scroll, string? Error);

/// <summary>
/// Everything the screen shows, in one immutable record. <see cref="Rows"/> is derived from the
/// status, filter, search and collapsed folders whenever one of them changes (in Update), so a view
/// only ever touches the rows it draws.
/// </summary>
public sealed record TuiState(
    TuiOptions Options,
    int Width,
    int Height,
    DateTimeOffset Now)
{
    public FolderStatus? Status { get; init; }

    /// <summary>Totals and counts of <see cref="Status"/>, set with it.</summary>
    public StatusSummary Summary { get; init; } = StatusSummary.Empty;

    public ImmutableArray<ListRow> Rows { get; init; } = [];

    /// <summary>The rows the list would have with no filter and no search: the footer's "of N rows".</summary>
    public int TotalRows { get; init; }

    public int Selected { get; init; }

    public int Scroll { get; init; }

    public ListFilter Filter { get; init; }

    public string Search { get; init; } = string.Empty;

    public bool Searching { get; init; }

    public ImmutableHashSet<string> Collapsed { get; init; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    public Screen Screen { get; init; }

    /// <summary>The job the session is running (any kind), for the header and the busy check.</summary>
    public SessionJob? Running { get; init; }

    /// <summary>A sync, resolve or push in progress: the progress view.</summary>
    public JobView? Job { get; init; }

    public Dialog? Dialog { get; init; }

    /// <summary>Newest first.</summary>
    public ImmutableList<ActivityLine> Activity { get; init; } = [];

    public Banner? Banner { get; init; }

    public DateTimeOffset? ScannedAt { get; init; }

    /// <summary>The last rescan's failure (share deleted, storage unreachable, …): <c>s</c> and <c>p</c> show it again instead of starting.</summary>
    public Error? Problem { get; init; }

    public int ConflictIndex { get; init; }

    public DiffState? Diff { get; init; }

    public bool IsBusy => Running is not null;

    public bool IsTooSmall => Width < 80 || Height < 24;

    /// <summary>Rows of the main list's viewport (8 … H−4).</summary>
    public int ListHeight => Math.Max(1, Height - 11);

    public ListRow? SelectedRow => Selected >= 0 && Selected < Rows.Length ? Rows[Selected] : null;

    public ImmutableArray<FolderEntry> ConflictEntries => Summary.Conflicts;
}
