using System.Globalization;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Session;
using MySync.Domain.Manifests;
using MySync.Domain.Sync;
using MySync.Tui.Rendering;
using MySync.Tui.State;

namespace MySync.Tui.Views;

/// <summary>
/// Every screen as a pure function of the state (CLAUDE.md §10d): <c>Render(state) → Frame</c>.
/// Views never talk to the terminal, and the list draws only the rows in its viewport.
/// </summary>
public static class Screens
{
    private const int StatusWidth = 22;
    private const int SizeWidth = 8;
    private const int DetailsWidth = 24;
    private const int DialogWidth = 64;
    private const int FieldWidth = 40;

    public static Frame Render(TuiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Frame frame = new(state.Width, state.Height);

        if (state.IsTooSmall)
        {
            TooSmall(frame, state);
            return frame;
        }

        Header(frame, state);
        if (state.Job is { } job)
        {
            Progress(frame, state, job);
        }
        else
        {
            switch (state.Screen)
            {
                case Screen.Conflicts:
                    Conflicts(frame, state);
                    break;
                case Screen.Diff:
                    Diff(frame, state);
                    break;
                default:
                    Main(frame, state);
                    break;
            }
        }

        if (state.Dialog is { } dialog)
        {
            frame.Dim();
            frame.Cursor = null;
            DialogBox(frame, state, dialog);
        }

        return frame;
    }

    // ----- shared parts -----

    private static void TooSmall(Frame frame, TuiState state)
    {
        string text = state.Options.Ascii ? "Make the window at least 80 x 24" : "Make the window at least 80 × 24";
        int x = Math.Max(0, (state.Width - Rendering.Text.Width(text)) / 2);
        frame.Put(x, state.Height / 2, text, new Style(Role.Text, Bold: true));
    }

    private static void Header(Frame frame, TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        FolderStatus? status = state.Status;
        int x = 1;
        x += frame.Put(x, 0, "mysync", new Style(Role.Share, Bold: true));
        x += frame.Put(x, 0, $" {symbols.Dot} ", new Style(Role.Muted));
        x += frame.Put(x, 0, status?.ShareLabel ?? "…", new Style(Role.Text, Bold: true));
        int room = state.Width - x - 3;
        RightAligned(frame, 0, Rendering.Text.FitLeft(state.Options.FolderPath, room, symbols.Ellipsis), new Style(Role.Muted));

        string left = status is null
            ? "reading the share…"
            : $"share revision {status.HeadRevision} {symbols.Dot} last changed by {status.HeadUpdatedBy ?? "?"}";
        string right = state.Running?.Kind switch
        {
            JobKind.Rescan => "scanning…",
            JobKind.Sync => "syncing…",
            JobKind.Resolve => "resolving…",
            JobKind.Push => "pushing…",
            _ => state.ScannedAt is { } at ? $"scanned {Rendering.Text.Ago(at, state.Now)}" : string.Empty,
        };
        int rightWidth = Rendering.Text.Width(right);
        frame.Put(1, 1, Rendering.Text.Fit(left, state.Width - rightWidth - 4, symbols.Ellipsis), new Style(Role.Muted));
        RightAligned(frame, 1, right, new Style(Role.Muted));
        Rule(frame, state, 2);
    }

    private static void Rule(Frame frame, TuiState state, int y) =>
        frame.Put(0, y, string.Concat(Enumerable.Repeat(state.Options.Symbols.Rule, state.Width)), new Style(Role.Rule));

    private static void RightAligned(Frame frame, int y, string text, Style style) =>
        frame.Put(Math.Max(0, frame.Width - 1 - Rendering.Text.Width(text)), y, text, style);

    /// <summary>The bottom key bar: key caps and labels, dropped from the right when they don't fit; <c>?</c> always stays.</summary>
    private static void KeyBar(Frame frame, TuiState state, IReadOnlyList<(string Key, string Label)> keys)
    {
        int y = state.Height - 1;
        Rule(frame, state, y - 1);
        (string Key, string Label) help = ("?", "Help");
        static int Width((string Key, string Label) hint) => Rendering.Text.Width(hint.Key) + 2 + 1 + Rendering.Text.Width(hint.Label) + 3;

        List<(string Key, string Label)> shown = [.. keys];
        // The last hint needs no gap after it.
        while (shown.Count > 0 && 2 + shown.Sum(Width) + Width(help) - 3 > state.Width)
        {
            shown.RemoveAt(shown.Count - 1);
        }

        shown.Add(help);
        int x = 2;
        foreach ((string key, string label) in shown)
        {
            x += frame.Put(x, y, $" {key} ", new Style(Role.KeyCapFg, Role.KeyCapBg, Bold: true));
            x += frame.Put(x, y, $" {label}", new Style(Role.Muted));
            x += 3;
        }
    }

    // ----- the main screen -----

    private static void Main(Frame frame, TuiState state)
    {
        Summary(frame, state);
        Rule(frame, state, 4);
        ShowRow(frame, state);
        BannerRow(frame, state, 6);
        ListView(frame, state);
        Footer(frame, state);

        int pending = state.Summary.PendingChanges;
        KeyBar(frame, state,
        [
            ("r", "Rescan"),
            ("s", pending > 0 ? $"Sync {Update.TuiUpdate.Count(pending, "change")}" : "Sync"),
            ("p", "Push"),
            ("c", "Conflicts"),
            ("f", "Filter"),
            ("q", "Quit"),
        ]);
    }

    private static void Summary(Frame frame, TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        if (state.Status is not { } status)
        {
            frame.Put(2, 3, state.Problem?.Description ?? "Scanning this folder…", new Style(state.Problem is null ? Role.Muted : Role.Conflict));
            return;
        }

        int x = 2;
        bool full = false;
        void Item(string text, Role role, string? detail = null)
        {
            int needed = Rendering.Text.Width(text) + (detail is null ? 0 : 1 + Rendering.Text.Width(detail));
            if (full || x + needed > state.Width - 2)
            {
                if (!full)
                {
                    frame.Put(x, 3, symbols.Ellipsis, new Style(Role.Muted));
                }

                full = true;
                return;
            }

            x += frame.Put(x, 3, text, new Style(role, Bold: role != Role.Muted));
            if (detail is not null)
            {
                x += frame.Put(x, 3, $" {detail}", new Style(Role.Muted));
            }

            x += 4;
        }

        if (status.IsShareEmpty)
        {
            int here = state.Summary.ToUpload.Files;
            string verb = here == 1 ? "isn't" : "aren't";
            string newShare = here > 0
                ? $"{Update.TuiUpdate.Count(here, "file")} here {verb} in the share yet. Press s to upload them."
                : "This share is new and empty.";
            frame.Put(2, 3, newShare, new Style(Role.Muted));
            return;
        }

        StatusSummary summary = state.Summary;
        PendingTotals upload = summary.ToUpload;
        PendingTotals download = summary.ToDownload;
        if (upload.Files > 0)
        {
            Item($"{symbols.Up} {upload.Files} to upload", Role.Here, Rendering.Text.Bytes(upload.Bytes));
        }

        if (download.Files > 0)
        {
            Item($"{symbols.Down} {download.Files} to download", Role.Share, Rendering.Text.Bytes(download.Bytes));
        }

        if (summary.ToDelete > 0)
        {
            Item($"{symbols.Delete} {summary.ToDelete} to delete", Role.Text);
        }

        if (summary.ConflictCount > 0)
        {
            Item($"{symbols.Conflict} {Update.TuiUpdate.Count(summary.ConflictCount, "conflict")}", Role.Conflict);
        }

        if (summary.SkippedCount > 0)
        {
            Item($"{summary.SkippedCount} skipped", Role.Muted);
        }

        if (x == 2)
        {
            frame.Put(2, 3, $"{symbols.Done} Everything is in sync", new Style(Role.Ok, Bold: true));
        }
    }

    private static void ShowRow(Frame frame, TuiState state)
    {
        IReadOnlyDictionary<ListFilter, int> counts = state.Summary.FilterCounts;
        int x = 1;
        x += frame.Put(x, 5, "Show:", new Style(Role.Muted)) + 2;
        foreach (ListFilter filter in Enum.GetValues<ListFilter>())
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"{filter} {counts[filter]}");
            x += state.Filter == filter
                ? frame.Put(x, 5, $" {label} ", new Style(Role.ChipFg, Role.ChipBg, Bold: true))
                : frame.Put(x, 5, $" {label} ", new Style(Role.Muted));
            x += 1;
        }

        if (state.Searching || state.Search.Length > 0)
        {
            string search = $"/ {state.Search}";
            RightAligned(frame, 5, search + (state.Searching ? " " : string.Empty), new Style(Role.Text, Bold: true));
            if (state.Searching)
            {
                frame.Cursor = (state.Width - 2, 5);
            }
        }
        else
        {
            RightAligned(frame, 5, "/ search", new Style(Role.Muted));
        }
    }

    private static void BannerRow(Frame frame, TuiState state, int y)
    {
        if (state.Banner is { } banner)
        {
            frame.Put(1, y, Rendering.Text.Fit(banner.Text, state.Width - 2, state.Options.Symbols.Ellipsis), new Style(banner.Role, Bold: banner.Role != Role.Muted));
        }
    }

    /// <summary>The list's columns at this width: Name flexible (W−60, at least 20); below 100 columns the Details column is dropped and its width goes to Name.</summary>
    public static (int Name, int Status, int Size, int Details) Columns(int width)
    {
        bool details = width >= 100;
        int name = details ? Math.Max(20, width - 60) : Math.Max(20, width - 36);
        return (name, StatusWidth, SizeWidth, details ? DetailsWidth : 0);
    }

    private static void ListView(Frame frame, TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        (int name, int status, int size, int details) = Columns(state.Width);
        int nameX = 3, statusX = nameX + name, sizeX = statusX + status, detailsX = sizeX + size + 2;
        bool muted = state.Running?.Kind == JobKind.Rescan && state.Status is not null;

        frame.Put(nameX, 7, "Name", new Style(Role.Muted));
        frame.Put(statusX, 7, "Status", new Style(Role.Muted));
        frame.Put(sizeX, 7, Rendering.Text.PadLeft("Size", size, symbols.Ellipsis), new Style(Role.Muted));
        if (details > 0)
        {
            frame.Put(detailsX, 7, "Details", new Style(Role.Muted));
        }

        int top = 8;
        int height = state.ListHeight;
        for (int i = 0; i < height; i++)
        {
            int index = state.Scroll + i;
            if (index >= state.Rows.Length)
            {
                break;
            }

            ListRow row = state.Rows[index];
            int y = top + i;
            bool selected = index == state.Selected;
            Role bg = selected ? Role.SelectionBg : Role.Background;
            if (selected)
            {
                frame.Fill(0, y, state.Width, 1, new Style(Role.Text, bg));
                frame.Put(1, y, symbols.Selected, new Style(Role.Text, bg, Bold: true));
            }

            (string statusText, Role statusRole) = RowStatus(row, symbols);
            Role nameRole = muted || row.Entry?.Status is FileStatus.DeletedHere or FileStatus.DeletedInShare or FileStatus.Ignored ? Role.Muted : Role.Text;
            string glyph = row.Expanded ? symbols.FolderOpen : symbols.FolderClosed;
            string label = row.IsFolder
                ? $"{new string(' ', row.Depth * 2)}{glyph} {row.Name}/"
                : $"{new string(' ', row.Depth * 2 + 2)}{row.Name}";

            frame.Put(nameX, y, Rendering.Text.Fit(label, name - 1, symbols.Ellipsis), new Style(nameRole, bg, Bold: selected));
            frame.Put(statusX, y, Rendering.Text.Fit(statusText, status - 1, symbols.Ellipsis), new Style(muted ? Role.Muted : statusRole, bg));
            if (!row.IsFolder && row.Entry?.Size is { } bytes)
            {
                frame.Put(sizeX, y, Rendering.Text.PadLeft(Rendering.Text.Bytes(bytes), size, symbols.Ellipsis), new Style(Role.Muted, bg));
            }

            if (details > 0 && row.Entry is { } entry && !row.IsFolder)
            {
                frame.Put(detailsX, y, Rendering.Text.Fit(Details(entry, state), details, symbols.Ellipsis), new Style(Role.Muted, bg));
            }
        }

        if (state.Rows.IsEmpty && state.Status is not null)
        {
            frame.Put(nameX, top, state.Search.Length > 0 || state.Filter != ListFilter.All ? "Nothing matches." : "This folder is empty.", new Style(Role.Muted));
        }
    }

    private static (string Text, Role Role) RowStatus(ListRow row, Symbols symbols)
    {
        if (row.IsFolder && row.Entry is null)
        {
            int changes = row.Changes + row.Conflicts;
            if (changes > 0)
            {
                return (Update.TuiUpdate.Count(changes, "change"), Role.Muted);
            }

            return (row.Skipped > 0 ? $"{row.Skipped} skipped" : string.Empty, Role.Muted);
        }

        return StatusText(row.Entry!.Status, symbols);
    }

    /// <summary>A status's symbol, word and colour; every status has a symbol and a word, never colour alone.</summary>
    public static (string Text, Role Role) StatusText(FileStatus status, Symbols symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        return status switch
        {
            FileStatus.NewHere => ($"{symbols.Up} new here", Role.Here),
            FileStatus.ChangedHere => ($"{symbols.Up} changed here", Role.Here),
            FileStatus.DeletedHere => ($"{symbols.Delete} deleted here", Role.Here),
            FileStatus.NewInShare => ($"{symbols.Down} new in share", Role.Share),
            FileStatus.ChangedInShare => ($"{symbols.Down} changed in share", Role.Share),
            FileStatus.DeletedInShare => ($"{symbols.Delete} deleted in share", Role.Share),
            FileStatus.Conflict => ($"{symbols.Conflict} conflict", Role.Conflict),
            FileStatus.Ignored => ($"{symbols.Waiting} ignored", Role.Muted),
            FileStatus.Skipped => ($"{symbols.Waiting} skipped", Role.Muted),
            _ => ($"{symbols.Done} synced", Role.Muted),
        };
    }

    private static string Details(FolderEntry entry, TuiState state)
    {
        string dot = state.Options.Symbols.Dot;
        return entry.Status switch
        {
            FileStatus.NewHere or FileStatus.ChangedHere => entry.MtimeUtc is { } mtime ? Rendering.Text.When(mtime, state.Now) : string.Empty,
            FileStatus.NewInShare or FileStatus.ChangedInShare => $"{entry.ChangedBy} {dot} rev {entry.ChangedAt}",
            FileStatus.Conflict => "both sides changed it",
            FileStatus.DeletedHere => "will leave the share",
            FileStatus.DeletedInShare => "will be deleted here",
            FileStatus.Skipped or FileStatus.Ignored => entry.Detail ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static void Footer(Frame frame, TuiState state)
    {
        int y = state.Height - 3;
        int total = state.TotalRows;
        frame.Put(1, y, $"{state.Rows.Length} of {total} rows", new Style(Role.Muted));
        if (state.Status is { } status)
        {
            string gzip = status.Compression == CompressionSetting.Gzip ? $" {state.Options.Symbols.Dot} gzip" : string.Empty;
            RightAligned(frame, y, $"storage {status.Location.AccountName}/{status.Location.Container}{gzip}", new Style(Role.Muted));
        }
    }

    // ----- a running job -----

    private static void Progress(Frame frame, TuiState state, JobView job)
    {
        Symbols symbols = state.Options.Symbols;
        int x = 2;
        x += frame.Put(x, 3, job.Title, new Style(Role.Text, Bold: true));
        frame.Put(x + 3, 3, Step(job.Phase), new Style(Role.Muted));

        int y = 5;
        if (job.Upload is { } upload && (upload.Files > 0 || upload.Bytes > 0))
        {
            Bar(frame, state, y, "Upload", Role.Here, upload);
            y += 3;
        }

        if (job.Download is { } download && (download.Files > 0 || download.Bytes > 0))
        {
            Bar(frame, state, y, "Download", Role.Share, download);
            y += 3;
        }

        Rule(frame, state, y);
        (int name, int status, int size, _) = Columns(state.Width);
        int nameX = 3, statusX = nameX + name, sizeX = statusX + status, detailsX = sizeX + size + 2;
        frame.Put(nameX, y + 1, $"In this {job.Job.Name}", new Style(Role.Muted));
        frame.Put(statusX, y + 1, "State", new Style(Role.Muted));
        frame.Put(sizeX, y + 1, Rendering.Text.PadLeft("Size", size, symbols.Ellipsis), new Style(Role.Muted));

        int top = y + 2;
        int rows = Math.Max(0, state.Height - 3 - top);
        int firstOpen = job.Items.IndexOf(job.Items.FirstOrDefault(item => item.State is ItemState.Active or ItemState.Waiting)!);
        int start = Math.Clamp(firstOpen < 0 ? job.Items.Length - rows : firstOpen - 2, 0, Math.Max(0, job.Items.Length - rows));
        for (int i = 0; i < rows && start + i < job.Items.Length; i++)
        {
            JobItem item = job.Items[start + i];
            (string text, Role role, string detail) = ItemText(item, symbols);
            frame.Put(nameX, top + i, Rendering.Text.Fit(item.Path, name - 1, symbols.Ellipsis), new Style(Role.Text));
            frame.Put(statusX, top + i, Rendering.Text.Fit(text, status - 1, symbols.Ellipsis), new Style(role));
            frame.Put(sizeX, top + i, Rendering.Text.PadLeft(Rendering.Text.Bytes(item.Size), size, symbols.Ellipsis), new Style(Role.Muted));
            if (state.Width >= 100)
            {
                frame.Put(detailsX, top + i, Rendering.Text.Fit(detail, DetailsWidth, symbols.Ellipsis), new Style(Role.Muted));
            }
        }

        frame.Put(1, state.Height - 3, job.Cancelling
            ? "Cancelling… a cancelled job can be run again."
            : $"Ctrl+C also cancels safely; a cancelled {job.Job.Name} can be run again.", new Style(Role.Muted));
        KeyBar(frame, state, [("Esc", "Cancel")]);
    }

    private static string Step(SyncPhase? phase) => phase switch
    {
        SyncPhase.Scanning => "step 1 of 4: scanning this folder",
        SyncPhase.Uploading => "step 2 of 4: uploading",
        SyncPhase.Committing => "step 3 of 4: recording the new revision",
        SyncPhase.Downloading => "step 4 of 4: downloading",
        _ => "starting…",
    };

    private static void Bar(Frame frame, TuiState state, int y, string label, Role role, DirectionProgress progress)
    {
        Symbols symbols = state.Options.Symbols;
        int barWidth = Math.Clamp(state.Width - 64, 20, 46);
        int filled = barWidth * progress.Percent / 100;
        frame.Put(2, y, label, new Style(role, Bold: true));
        int x = 12;
        x += frame.Put(x, y, string.Concat(Enumerable.Repeat(symbols.BarFull, filled)), new Style(role));
        x += frame.Put(x, y, string.Concat(Enumerable.Repeat(symbols.BarEmpty, barWidth - filled)), new Style(Role.BarEmpty));
        string speed = progress.Rate > 0 ? $" {symbols.Dot} {Rendering.Text.Bytes(progress.Rate)}/s" : string.Empty;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"  {progress.Percent}%  {progress.FilesDone} of {Update.TuiUpdate.Count(progress.Files, "file")} {symbols.Dot} {Rendering.Text.BytesOf(progress.BytesDone, progress.Bytes)}{speed}");
        frame.Put(x, y, Rendering.Text.Fit(text, state.Width - x - 1, symbols.Ellipsis), new Style(Role.Text));
        if (progress.Current is { } current)
        {
            frame.Put(12, y + 1, Rendering.Text.FitLeft(current, state.Width - 14, symbols.Ellipsis), new Style(Role.Muted));
        }
    }

    private static (string Text, Role Role, string Detail) ItemText(JobItem item, Symbols symbols) => (item.State, item.Direction) switch
    {
        (ItemState.Conflict, _) => ($"{symbols.Conflict} conflict", Role.Conflict, "left as it is"),
        (ItemState.Done, TransferDirection.Upload) => ($"{symbols.Done} {(item.Action == "push" ? "pushed" : "uploaded")}", Role.Ok, string.Empty),
        (ItemState.Done, TransferDirection.Download) => ($"{symbols.Done} downloaded", Role.Ok, string.Empty),
        (ItemState.Done, _) => ($"{symbols.Done} done", Role.Ok, string.Empty),
        (ItemState.Active, TransferDirection.Upload) => ($"{symbols.Up} {(item.Action == "push" ? "pushing" : "uploading")}", Role.Here, string.Empty),
        (ItemState.Active, TransferDirection.Download) => ($"{symbols.Down} downloading", Role.Share, string.Empty),
        _ => ($"{symbols.Waiting} waiting", Role.Muted, item.Action),
    };

    // ----- conflicts -----

    private static void Conflicts(Frame frame, TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        System.Collections.Immutable.ImmutableArray<FolderEntry> conflicts = state.ConflictEntries;
        int x = 2;
        x += frame.Put(x, 3, "Conflicts", new Style(Role.Text, Bold: true));
        if (conflicts.Length > 0)
        {
            bool one = conflicts.Length == 1;
            frame.Put(x + 2, 3, $"{conflicts.Length} {(one ? "needs" : "need")} a decision. Both sides changed {(one ? "it" : "them")}; nothing was overwritten.", new Style(Role.Muted));
        }

        BannerRow(frame, state, 4);
        int y = 5;
        int activityTop = state.Height - 3 - Math.Min(6, Math.Max(2, state.Activity.Count + 2));
        if (conflicts.Length == 0)
        {
            frame.Put(2, y, $"{symbols.Done} No conflicts.", new Style(Role.Ok, Bold: true));
        }
        else
        {
            int index = Math.Clamp(state.ConflictIndex, 0, conflicts.Length - 1);
            int start = Math.Max(0, index - 3);
            for (int i = start; i < conflicts.Length && y < activityTop - 1; i++)
            {
                FolderEntry entry = conflicts[i];
                bool selected = i == index;
                Role bg = selected ? Role.SelectionBg : Role.Background;
                if (selected)
                {
                    frame.Fill(0, y, state.Width, 1, new Style(Role.Text, bg));
                    frame.Put(1, y, symbols.Selected, new Style(Role.Text, bg, Bold: true));
                }

                int nameWidth = frame.Put(3, y, Rendering.Text.Fit(entry.Path, state.Width - 40, symbols.Ellipsis), new Style(Role.Text, bg, Bold: true));
                frame.Put(3 + nameWidth + 3, y, Kind(entry.ConflictKind), new Style(Role.Conflict, bg));
                y++;
                if (selected && y + 6 < activityTop)
                {
                    y = Comparison(frame, state, entry, y + 1);
                }
            }
        }

        Rule(frame, state, activityTop);
        frame.Put(2, activityTop + 1, "This session", new Style(Role.Text, Bold: true));
        Activity(frame, state, activityTop + 2, state.Height - 3);
        KeyBar(frame, state, [("m", "Keep mine"), ("t", "Keep theirs"), ("M", "All mine"), ("T", "All theirs"), ("d", "Diff"), ("Esc", "Back")]);
    }

    private static int Comparison(Frame frame, TuiState state, FolderEntry entry, int y)
    {
        Symbols symbols = state.Options.Symbols;
        Conflict? conflict = state.Status?.Conflicts.FirstOrDefault(c => c.Path.Value == entry.Path);
        int left = 5, right = 5 + Math.Max(30, (state.Width - 10) / 2);
        frame.Put(left, y, "Yours, in this folder", new Style(Role.Here, Bold: true));
        frame.Put(right, y, "Theirs, in the share", new Style(Role.Share, Bold: true));

        void Side(int x, ManifestEntry? side, string who)
        {
            if (side is not FileEntry file)
            {
                frame.Put(x, y + 1, side is DirectoryEntry ? "a folder" : "deleted", new Style(Role.Text));
                frame.Put(x, y + 3, who, new Style(Role.Muted));
                return;
            }

            frame.Put(x, y + 1, Rendering.Text.Bytes(file.Size), new Style(Role.Text));
            frame.Put(x, y + 2, $"modified {Rendering.Text.When(file.MtimeUtc, state.Now)}", new Style(Role.Muted));
            frame.Put(x, y + 3, Rendering.Text.Fit(who, right - left - 2, symbols.Ellipsis), new Style(Role.Muted));
        }

        Side(left, conflict?.Local, state.Options.MachineName.Length > 0 ? state.Options.MachineName : "this machine");
        Side(right, conflict?.Remote, conflict is null ? string.Empty : $"{conflict.RemoteUpdatedBy} {symbols.Dot} revision {conflict.RemoteRevision}");
        string hint = Update.TuiUpdate.IsRecorded(state, entry.Path)
            ? "m keeps yours: uploads your file.   t keeps theirs: replaces your file with the share's."
            : "Found by the rescan. Press s to sync first (it leaves this file as it is), then m or t decides it.";
        frame.Put(left, y + 5, Rendering.Text.Fit(hint, state.Width - left - 1, symbols.Ellipsis), new Style(Role.Muted));
        return y + 7;
    }

    private static string Kind(ConflictKind? kind) => kind switch
    {
        ConflictKind.AddAdd => "added on both sides",
        ConflictKind.ModifyDelete => "changed here, deleted in the share",
        ConflictKind.DeleteModify => "deleted here, changed in the share",
        ConflictKind.Type => "a file on one side, a folder on the other",
        _ => "changed on both sides",
    };

    private static void Activity(Frame frame, TuiState state, int top, int bottom)
    {
        int y = top;
        foreach (ActivityLine line in state.Activity)
        {
            if (y >= bottom)
            {
                break;
            }

            int x = 2 + frame.Put(2, y, line.Time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture), new Style(Role.Muted)) + 2;
            frame.Put(x, y, Rendering.Text.Fit(line.Text, state.Width - x - 1, state.Options.Symbols.Ellipsis), new Style(line.Role == Role.Muted ? Role.Text : line.Role));
            y++;
        }
    }

    // ----- the diff -----

    private static void Diff(Frame frame, TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        if (state.Diff is not { } diff)
        {
            return;
        }

        if (diff.Loading)
        {
            frame.Put(2, 3, $"Loading the diff of {diff.Path}…", new Style(Role.Muted));
        }
        else if (diff.Error is { } error)
        {
            frame.Put(2, 3, Rendering.Text.Fit(error, state.Width - 4, symbols.Ellipsis), new Style(Role.Conflict));
        }
        else
        {
            int rows = state.Height - 6;
            for (int i = 0; i < rows && diff.Scroll + i < diff.Lines.Length; i++)
            {
                DiffLineView line = diff.Lines[diff.Scroll + i];
                frame.Put(1, 3 + i, Rendering.Text.Fit(line.Text, state.Width - 2, symbols.Ellipsis), new Style(line.Role, Bold: diff.Scroll + i == 0));
            }

            frame.Put(1, state.Height - 3, string.Create(CultureInfo.InvariantCulture, $"lines {Math.Min(diff.Lines.Length, diff.Scroll + 1)}–{Math.Min(diff.Lines.Length, diff.Scroll + rows)} of {diff.Lines.Length}"), new Style(Role.Muted));
        }

        KeyBar(frame, state, [($"{symbols.Up}{symbols.Down}", "Scroll"), ("m", "Keep mine"), ("t", "Keep theirs"), ("Esc", "Back")]);
    }

    // ----- dialogs -----

    private static void DialogBox(Frame frame, TuiState state, Dialog dialog)
    {
        List<DialogLine> lines = dialog switch
        {
            PushDialog push => PushLines(state, push),
            ConfirmDialog confirm => ConfirmLines(confirm),
            _ => HelpLines(state),
        };

        Symbols symbols = state.Options.Symbols;
        int width = Math.Min(DialogWidth, state.Width - 4);
        int x = (state.Width - width) / 2;
        int y = 3;
        int height = Math.Min(lines.Count + 2, state.Height - y - 1);
        Style border = new(Role.Muted, Role.DialogBg);
        frame.Fill(x, y, width, height, new Style(Role.Text, Role.DialogBg));
        frame.Put(x, y, symbols.BoxTopLeft + string.Concat(Enumerable.Repeat(symbols.BoxHorizontal, width - 2)) + symbols.BoxTopRight, border);
        frame.Put(x, y + height - 1, symbols.BoxBottomLeft + string.Concat(Enumerable.Repeat(symbols.BoxHorizontal, width - 2)) + symbols.BoxBottomRight, border);
        for (int row = 1; row < height - 1; row++)
        {
            frame.Put(x, y + row, symbols.BoxVertical, border);
            frame.Put(x + width - 1, y + row, symbols.BoxVertical, border);
        }

        int inner = width - 4;
        for (int i = 0; i < height - 2; i++)
        {
            DialogLine line = lines[i];
            int cx = x + 2;
            foreach (DialogPart part in line.Parts)
            {
                Style style = new(part.Role, part.Bg ?? Role.DialogBg, part.Bold);
                string text = part.Width is { } fixedWidth ? Rendering.Text.Pad(part.Text, fixedWidth, symbols.Ellipsis) : part.Text;
                cx += frame.Put(cx, y + 1 + i, Rendering.Text.Fit(text, x + 2 + inner - cx, symbols.Ellipsis), style);
                if (part.Cursor)
                {
                    frame.Cursor = (Math.Min(cx - (part.Width ?? 0) + Rendering.Text.Width(part.Text), x + width - 3), y + 1 + i);
                }
            }
        }
    }

    private sealed record DialogPart(string Text, Role Role = Role.Text, bool Bold = false, Role? Bg = null, int? Width = null, bool Cursor = false);

    private sealed record DialogLine(params DialogPart[] Parts)
    {
        public static DialogLine Blank { get; } = new();
    }

    private static List<DialogLine> PushLines(TuiState state, PushDialog push)
    {
        string item = push.ItemPath + (push.ItemIsFolder ? "/" : string.Empty);
        string summary = push.ItemIsFolder ? $"{Update.TuiUpdate.Count(push.Files, "file")} {state.Options.Symbols.Dot} {Rendering.Text.Bytes(push.Bytes)}" : Rendering.Text.Bytes(push.Bytes);
        string share = state.Status?.ShareLabel ?? "this share";
        List<DialogLine> lines =
        [
            new(new DialogPart("Push a copy to another share", Bold: true)),
            DialogLine.Blank,
            new(new DialogPart("Item   ", Role.Muted), new DialogPart(item, Bold: true), new DialogPart($"  {summary}", Role.Muted)),
        ];
        lines.AddRange(Wrap($"Uploads it as it is now. Nothing here or in {share} changes.", DialogWidth - 6).Select(text => new DialogLine(new DialogPart(text, Role.Muted))));
        lines.Add(DialogLine.Blank);
        lines.Add(new DialogLine(new DialogPart("Share name", Role.Muted)));
        lines.Add(new DialogLine(new DialogPart(" "), new DialogPart(push.Share, Role.Text, push.Focus == PushField.Share, Role.FieldBg, FieldWidth, Cursor: push.Focus == PushField.Share)));
        lines.Add(push.Error is { } error ? new DialogLine(new DialogPart(error, Role.Conflict)) : DialogLine.Blank);
        lines.Add(new DialogLine(new DialogPart($"[{(push.Create ? "x" : " ")}] Create the share if it doesn't exist", Role.Text, push.Focus == PushField.Create)));
        lines.Add(DialogLine.Blank);
        lines.Add(new DialogLine(new DialogPart("Lands in  ", Role.Muted), new DialogPart(Update.TuiUpdate.LandsIn(state, push.ItemPath, push.ItemIsFolder))));
        lines.Add(DialogLine.Blank);
        lines.Add(Keys(("Enter", "Push"), ("Tab", "Next field"), ("Esc", "Cancel")));
        return lines;
    }

    private static List<DialogLine> ConfirmLines(ConfirmDialog confirm)
    {
        List<DialogLine> lines = [new(new DialogPart(confirm.Title, Bold: true)), DialogLine.Blank];
        foreach (string text in confirm.Lines)
        {
            lines.AddRange(Wrap(text, DialogWidth - 6).Select(line => new DialogLine(new DialogPart(line, Role.Muted))));
        }

        lines.Add(DialogLine.Blank);
        lines.Add(Keys(("y", confirm.YesLabel), ("n", "Cancel")));
        return lines;
    }

    private static List<DialogLine> HelpLines(TuiState state)
    {
        Symbols symbols = state.Options.Symbols;
        (string Key, string Label)[] keys =
        [
            ($"{symbols.Up} {symbols.Down}  k j", "move the highlight"),
            ("PgUp PgDn Home End", "page; first / last row"),
            ($"{symbols.FolderClosed} Enter", "open a folder; left closes it"),
            ("r", "rescan this folder"),
            ("s", "sync with the share"),
            ("p", "push the highlighted item to another share"),
            ("c", "conflicts: m / t keep mine / theirs, d diff"),
            ("f F  1-6", "next / previous filter, or pick one"),
            ("/", "search by name"),
            ("Esc", "close, go back, clear the search, cancel a job"),
            ("q", "quit"),
            ("Ctrl+C", "cancel the running job; with none, quit"),
        ];

        List<DialogLine> lines = [new(new DialogPart("Keys", Bold: true)), DialogLine.Blank];
        lines.AddRange(keys.Select(key => new DialogLine(new DialogPart(key.Key, Role.Text, Bold: true, Width: 20), new DialogPart(key.Label, Role.Muted))));
        lines.Add(DialogLine.Blank);
        lines.Add(Keys(("Esc", "Close")));
        return lines;
    }

    private static DialogLine Keys(params (string Key, string Label)[] keys) => new([.. keys.SelectMany(key => new[]
    {
        new DialogPart($" {key.Key} ", Role.KeyCapFg, Bold: true, Bg: Role.KeyCapBg),
        new DialogPart($" {key.Label}    ", Role.Muted),
    })]);

    /// <summary>Words wrapped to <paramref name="width"/> cells.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = [];
        System.Text.StringBuilder line = new();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }
}
