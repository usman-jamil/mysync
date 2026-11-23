using System.Collections.Immutable;
using System.Globalization;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Session;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Diffing;
using MySync.Domain.Session;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Domain.Sync.Push;
using MySync.Tui.Effects;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using SharedKernel;

namespace MySync.Tui.Update;

/// <summary>
/// The interface's logic (CLAUDE.md §10d): <c>Update(state, msg) → (state, effects)</c>. Pure: no
/// I/O, no clock, no terminal. Every key on every screen, the dialogs, and the session's events.
/// </summary>
public static class TuiUpdate
{
    private const int MaxActivity = 200;

    private static readonly ImmutableArray<Effect> None = [];

    public static TuiState Initial(TuiOptions options, int width, int height, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        TuiState state = new(options, width, height, now);
        foreach (string note in options.StartNotes.IsDefault ? [] : options.StartNotes)
        {
            state = Note(state, note, Role.Muted);
        }

        return state;
    }

    public static (TuiState State, ImmutableArray<Effect> Effects) Update(TuiState state, Msg message)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            KeyPressed pressed => OnKey(state, pressed.Key),
            Resized resized => (Clamp(state with { Width = resized.Width, Height = resized.Height }), None),
            Tick tick => (state with { Now = tick.Now }, None),
            SessionMsg session => (OnSession(state, session.Event), None),
            DiffLoaded loaded => (OnDiffLoaded(state, loaded), None),
            Refused refused => (Refuse(state, refused.Error), None),
            LogLine line => (Note(state, line.Text, Role.Conflict), None),
            _ => (state, None),
        };
    }

    // ----- keys -----

    private static (TuiState, ImmutableArray<Effect>) OnKey(TuiState state, KeyInput key)
    {
        if (key.Code == KeyCode.CtrlC)
        {
            return OnCtrlC(state);
        }

        if (state.IsTooSmall)
        {
            return key.Is('q') ? QuitNow(state, exitCode: 0) : (state, None);
        }

        if (state.Dialog is not null)
        {
            return OnDialogKey(state, key);
        }

        if (state.Searching)
        {
            return (OnSearchKey(state, key), None);
        }

        if (state.Job is not null)
        {
            return OnJobKey(state, key);
        }

        return state.Screen switch
        {
            Screen.Conflicts => OnConflictsKey(state, key),
            Screen.Diff => OnDiffKey(state, key),
            _ => OnMainKey(state, key),
        };
    }

    /// <summary>Ctrl+C cancels the running job; a second one while it cancels ends the interface (130); with no job, it quits.</summary>
    private static (TuiState, ImmutableArray<Effect>) OnCtrlC(TuiState state)
    {
        if (!state.IsBusy)
        {
            return (state, [new Quit(0)]);
        }

        if (state.Job is { Cancelling: true })
        {
            return (state, [new CancelJob(), new Quit(130)]);
        }

        return Cancel(state);
    }

    private static (TuiState, ImmutableArray<Effect>) QuitNow(TuiState state, int exitCode) =>
        state.IsBusy ? (state, [new CancelJob(), new Quit(exitCode)]) : (state, [new Quit(exitCode)]);

    private static (TuiState, ImmutableArray<Effect>) Cancel(TuiState state) =>
        (state with { Job = state.Job is { } job ? job with { Cancelling = true } : null, Banner = new Banner($"Cancelling the {state.Running?.Name}…", Role.Muted) }, [new CancelJob()]);

    /// <summary>While a sync, resolve or push runs: Esc cancels, q asks, ? helps; anything that would start a job says to wait.</summary>
    private static (TuiState, ImmutableArray<Effect>) OnJobKey(TuiState state, KeyInput key)
    {
        if (key.Code == KeyCode.Escape)
        {
            return Cancel(state);
        }

        if (key.Is('q'))
        {
            return (AskToQuit(state), None);
        }

        if (key.Is('?'))
        {
            return (state with { Dialog = new HelpDialog() }, None);
        }

        return IsJobKey(key) ? (Busy(state), None) : (state, None);
    }

    private static bool IsJobKey(KeyInput key) => key.Code == KeyCode.Character && "rspmtMT".Contains(key.Character, StringComparison.Ordinal);

    private static TuiState Busy(TuiState state) =>
        state with { Banner = new Banner($"Wait for the {state.Running?.Name ?? "job"} to finish, or press Esc to cancel it.", Role.Muted) };

    private static TuiState AskToQuit(TuiState state) => state with
    {
        Dialog = new ConfirmDialog(
            ConfirmKind.QuitDuringJob,
            "Quit while a job is running?",
            [$"A {state.Running?.Name ?? "job"} is running. Quit cancels it; it can be run again."],
            "Cancel and quit"),
    };

    private static (TuiState, ImmutableArray<Effect>) OnMainKey(TuiState state, KeyInput key)
    {
        switch (key.Code)
        {
            case KeyCode.Up:
                return (Move(state, -1), None);
            case KeyCode.Down:
                return (Move(state, 1), None);
            case KeyCode.PageUp:
                return (Move(state, -state.ListHeight), None);
            case KeyCode.PageDown:
                return (Move(state, state.ListHeight), None);
            case KeyCode.Home:
                return (Select(state, 0), None);
            case KeyCode.End:
                return (Select(state, state.Rows.Length - 1), None);
            case KeyCode.Right:
            case KeyCode.Enter:
                return (Expand(state), None);
            case KeyCode.Left:
                return (CollapseOrUp(state), None);
            case KeyCode.Escape:
                return state.Search.Length > 0 ? (WithRows(state with { Search = string.Empty }), None) : (state, None);
            case KeyCode.Character:
                break;
            default:
                return (state, None);
        }

        return key.Character switch
        {
            'k' => (Move(state, -1), None),
            'j' => (Move(state, 1), None),
            'r' => StartRescan(state),
            's' => StartSync(state),
            'p' => (OpenPush(state), None),
            'c' => (state with { Screen = Screen.Conflicts, ConflictIndex = 0, Banner = null }, None),
            'f' => (WithFilter(state, (ListFilter)(((int)state.Filter + 1) % 6)), None),
            'F' => (WithFilter(state, (ListFilter)(((int)state.Filter + 5) % 6)), None),
            >= '1' and <= '6' => (WithFilter(state, (ListFilter)(key.Character - '1')), None),
            '/' => (state with { Searching = true }, None),
            '?' => (state with { Dialog = new HelpDialog() }, None),
            'q' => state.IsBusy ? (AskToQuit(state), None) : (state, [new Quit(0)]),
            _ => (state, None),
        };
    }

    private static (TuiState, ImmutableArray<Effect>) StartRescan(TuiState state) =>
        state.IsBusy ? (Busy(state), None) : (state with { Running = SessionJob.Rescan }, [new StartRescan()]);

    private static (TuiState, ImmutableArray<Effect>) StartSync(TuiState state)
    {
        if (state.IsBusy)
        {
            return (Busy(state), None);
        }

        if (state.Problem is { } problem)
        {
            return (state with { Banner = new Banner(problem.Description, Role.Conflict) }, None);
        }

        if (state.Status is not { } status)
        {
            return (state, None);
        }

        PendingTotals upload = state.Summary.ToUpload;
        if (status.IsShareEmpty && (upload.Files > state.Options.LargeFileCount || upload.Bytes > state.Options.LargeTotalBytes))
        {
            return (state with
            {
                Dialog = new ConfirmDialog(
                    ConfirmKind.LargeUpload,
                    "Upload this folder to the new share?",
                    [
                        $"This first sync uploads {Count(upload.Files, "file")} ({Text.Bytes(upload.Bytes)}).",
                        "Later syncs send only what changed.",
                    ],
                    "Upload and sync"),
            }, None);
        }

        return (state with { Running = SessionJob.Sync }, [new StartSync(AllowMassDelete: false)]);
    }

    private static TuiState OpenPush(TuiState state)
    {
        if (state.IsBusy)
        {
            return Busy(state);
        }

        if (state.Problem is { } problem)
        {
            return state with { Banner = new Banner(problem.Description, Role.Conflict) };
        }

        if (state.SelectedRow is not { } row || state.Status is not { } status)
        {
            return state;
        }

        string prefix = row.Path + "/";
        List<FolderEntry> onDisk = [.. status.Entries.Where(entry =>
            entry.ExistsHere && !entry.IsFolder && (entry.Path == row.Path || row.IsFolder && entry.Path.StartsWith(prefix, StringComparison.Ordinal)))];
        bool emptyFolderHere = row.IsFolder && row.Entry is { ExistsHere: true };
        if (onDisk.Count == 0 && !emptyFolderHere)
        {
            return state with { Banner = new Banner("Nothing on disk to push for this item.", Role.Muted) };
        }

        return state with
        {
            Dialog = new PushDialog(row.Path, row.IsFolder, onDisk.Count, onDisk.Sum(entry => entry.Size ?? 0), string.Empty, Create: false, PushField.Share, Error: null),
            Banner = null,
        };
    }

    private static TuiState OnSearchKey(TuiState state, KeyInput key) => key.Code switch
    {
        KeyCode.Escape => WithRows(state with { Search = string.Empty, Searching = false }, resetSelection: true),
        KeyCode.Enter => state with { Searching = false },
        KeyCode.Backspace => WithRows(state with { Search = DropLast(state.Search) }, resetSelection: true),
        KeyCode.Character when !char.IsControl(key.Character) => WithRows(state with { Search = state.Search + key.Character }, resetSelection: true),
        _ => state,
    };

    private static (TuiState, ImmutableArray<Effect>) OnConflictsKey(TuiState state, KeyInput key)
    {
        ImmutableArray<FolderEntry> conflicts = state.ConflictEntries;
        FolderEntry? current = state.ConflictIndex < conflicts.Length ? conflicts[state.ConflictIndex] : null;

        switch (key.Code)
        {
            case KeyCode.Up:
                return (state with { ConflictIndex = Math.Max(0, state.ConflictIndex - 1) }, None);
            case KeyCode.Down:
                return (state with { ConflictIndex = Math.Clamp(state.ConflictIndex + 1, 0, Math.Max(0, conflicts.Length - 1)) }, None);
            case KeyCode.Escape:
                return (state with { Screen = Screen.Main }, None);
            case KeyCode.Character:
                break;
            default:
                return (state, None);
        }

        return key.Character switch
        {
            'k' => OnConflictsKey(state, KeyInput.Of(KeyCode.Up)),
            'j' => OnConflictsKey(state, KeyInput.Of(KeyCode.Down)),
            'm' or 't' when current is not null && !IsRecorded(state, current.Path) => (NotRecorded(state), None),
            'M' or 'T' when conflicts.Length > 0 && !conflicts.Any(entry => IsRecorded(state, entry.Path)) => (NotRecorded(state), None),
            'm' when current is not null => Resolve(state, [current.Path], all: false, KeepSide.Local),
            't' when current is not null => Resolve(state, [current.Path], all: false, KeepSide.Remote),
            'M' when conflicts.Length > 0 => Resolve(state, [], all: true, KeepSide.Local),
            'T' when conflicts.Length > 0 => Resolve(state, [], all: true, KeepSide.Remote),
            'd' when current is not null => (state with { Screen = Screen.Diff, Diff = new DiffState(current.Path, Loading: true, [], 0, null) }, [new LoadDiff(current.Path)]),
            '?' => (state with { Dialog = new HelpDialog() }, None),
            'q' => state.IsBusy ? (AskToQuit(state), None) : (state, [new Quit(0)]),
            _ => (state, None),
        };
    }

    /// <summary>A sync records a conflict (and leaves it as it is); only then can resolve decide it.</summary>
    internal static bool IsRecorded(TuiState state, string path) => state.Status?.IsRecorded(path) == true;

    private static TuiState NotRecorded(TuiState state) => state with
    {
        Banner = new Banner("Press s to sync first: a sync records the conflict and leaves it as it is. Then m or t decides it.", Role.Muted),
    };

    private static (TuiState, ImmutableArray<Effect>) Resolve(TuiState state, ImmutableArray<string> paths, bool all, KeepSide keep) =>
        state.IsBusy ? (Busy(state), None) : (state with { Running = SessionJob.Resolve }, [new StartResolve(paths, all, keep)]);

    private static (TuiState, ImmutableArray<Effect>) OnDiffKey(TuiState state, KeyInput key)
    {
        if (state.Diff is not { } diff)
        {
            return (state with { Screen = Screen.Conflicts }, None);
        }

        int page = Math.Max(1, state.Height - 6);
        int last = Math.Max(0, diff.Lines.Length - page);
        TuiState Scrolled(int to) => state with { Diff = diff with { Scroll = Math.Clamp(to, 0, last) } };

        return key.Code switch
        {
            KeyCode.Up => (Scrolled(diff.Scroll - 1), None),
            KeyCode.Down => (Scrolled(diff.Scroll + 1), None),
            KeyCode.PageUp => (Scrolled(diff.Scroll - page), None),
            KeyCode.PageDown => (Scrolled(diff.Scroll + page), None),
            KeyCode.Home => (Scrolled(0), None),
            KeyCode.End => (Scrolled(last), None),
            KeyCode.Escape => (state with { Screen = Screen.Conflicts, Diff = null }, None),
            KeyCode.Character when key.Character == 'k' => (Scrolled(diff.Scroll - 1), None),
            KeyCode.Character when key.Character == 'j' => (Scrolled(diff.Scroll + 1), None),
            KeyCode.Character when key.Character is 'm' or 't' && !IsRecorded(state, diff.Path) => (NotRecorded(state), None),
            KeyCode.Character when key.Character == 'm' => Resolve(state with { Screen = Screen.Conflicts, Diff = null }, [diff.Path], all: false, KeepSide.Local),
            KeyCode.Character when key.Character == 't' => Resolve(state with { Screen = Screen.Conflicts, Diff = null }, [diff.Path], all: false, KeepSide.Remote),
            KeyCode.Character when key.Character == '?' => (state with { Dialog = new HelpDialog() }, None),
            _ => (state, None),
        };
    }

    // ----- dialogs -----

    private static (TuiState, ImmutableArray<Effect>) OnDialogKey(TuiState state, KeyInput key) => state.Dialog switch
    {
        PushDialog push => OnPushKey(state, push, key),
        ConfirmDialog confirm => OnConfirmKey(state, confirm, key),
        _ => key.Code == KeyCode.Escape || key.Is('?') || key.Is('q') ? (state with { Dialog = null }, None) : (state, None),
    };

    private static (TuiState, ImmutableArray<Effect>) OnPushKey(TuiState state, PushDialog push, KeyInput key)
    {
        switch (key.Code)
        {
            case KeyCode.Escape:
                return (state with { Dialog = null }, None);
            case KeyCode.Tab:
                return (state with { Dialog = push with { Focus = push.Focus == PushField.Share ? PushField.Create : PushField.Share } }, None);
            case KeyCode.Enter:
                if (push.Share.Trim().Length == 0 || push.Error is not null)
                {
                    return (state, None);
                }

                return (
                    state with { Dialog = null, Running = new SessionJob(JobKind.Push, "push", push.ItemPath, push.Share.Trim()) },
                    [new StartPush(push.ItemPath, push.Share.Trim(), push.Create)]);
            case KeyCode.Backspace when push.Focus == PushField.Share:
                return (WithShare(state, push, DropLast(push.Share)), None);
            case KeyCode.Character when push.Focus == PushField.Create && key.Character == ' ':
                return (state with { Dialog = push with { Create = !push.Create } }, None);
            case KeyCode.Character when push.Focus == PushField.Share && !char.IsControl(key.Character):
                return (WithShare(state, push, push.Share + key.Character), None);
            default:
                return (state, None);
        }
    }

    private static TuiState WithShare(TuiState state, PushDialog push, string share) =>
        state with { Dialog = push with { Share = share, Error = PushError(state, share) } };

    /// <summary>The line under the share field: an invalid name, or this folder's own share.</summary>
    internal static string? PushError(TuiState state, string share)
    {
        string text = share.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        Result<ShareReference> parsed = ShareReference.Parse(text);
        if (parsed.IsFailure)
        {
            return parsed.Error.Description;
        }

        if (state.Status is { } status && (parsed.Value.Id == status.ShareId || parsed.Value.Name is { } name && status.Names.Contains(name)))
        {
            return SessionErrors.PushToOwnShare(status.ShareLabel).Description;
        }

        return null;
    }

    private static (TuiState, ImmutableArray<Effect>) OnConfirmKey(TuiState state, ConfirmDialog confirm, KeyInput key)
    {
        if (key.Code == KeyCode.Escape || key.Is('n'))
        {
            return (state with { Dialog = null }, None);
        }

        if (!key.Is('y'))
        {
            return (state, None); // Enter alone does nothing: the safe answer is the default.
        }

        TuiState closed = state with { Dialog = null };
        return confirm.Kind switch
        {
            ConfirmKind.MassDelete => (closed with { Running = SessionJob.Sync }, [new StartSync(AllowMassDelete: true)]),
            ConfirmKind.LargeUpload => (closed with { Running = SessionJob.Sync }, [new StartSync(AllowMassDelete: false)]),
            _ => (closed, [new CancelJob(), new Quit(0)]),
        };
    }

    // ----- session events -----

    private static TuiState OnSession(TuiState state, SessionEvent sessionEvent) => sessionEvent switch
    {
        JobStarted started => OnJobStarted(state, started.Job),
        JobFinished finished => OnJobFinished(state, finished),
        JobFailed failed => OnJobFailed(state, failed),
        JobCancelled cancelled => Note(state with { Running = null, Job = null, Banner = new Banner($"{Capital(cancelled.Job.Name)} cancelled. It can be run again.", Role.Muted) }, $"{Capital(cancelled.Job.Name)} cancelled.", Role.Muted),
        PhaseStarted phase => state.Job is { } job ? state with { Job = job with { Phase = phase.Phase } } : state,
        TransfersPlanned planned => WithDirection(state, planned.Direction, _ => DirectionProgress.Planned(planned.Files, planned.Bytes)),
        TransferProgress progress => WithDirection(state, progress.Direction, d => d with { BytesDone = progress.BytesDone, Rate = progress.BytesPerSecond }),
        FileStarted file => WithItem(WithDirection(state, file.Direction, d => d with { Current = file.Path.Value }), file.Path.Value, ItemState.Active),
        FileFinished file => WithItem(WithDirection(state, file.Direction, d => d with { FilesDone = Math.Min(d.Files, d.FilesDone + 1) }), file.Path.Value, ItemState.Done),
        _ => state,
    };

    private static TuiState OnJobStarted(TuiState state, SessionJob job)
    {
        if (job.Kind == JobKind.Rescan)
        {
            return state with { Running = job };
        }

        return state with { Running = job, Job = NewJob(state, job), Banner = null, Dialog = state.Dialog is HelpDialog ? null : state.Dialog };
    }

    private static JobView NewJob(TuiState state, SessionJob job)
    {
        IEnumerable<FolderEntry> entries = state.Status?.Entries ?? [];
        ImmutableArray<JobItem> items;
        string title;

        switch (job.Kind)
        {
            case JobKind.Push:
                string prefix = job.PushPath + "/";
                items = [.. entries
                    .Where(entry => entry.ExistsHere && !entry.IsFolder && (entry.Path == job.PushPath || entry.Path.StartsWith(prefix, StringComparison.Ordinal)))
                    .Select(entry => new JobItem(entry.Path, TransferDirection.Upload, "push", entry.Size ?? 0, ItemState.Waiting))];
                bool folder = items.Length != 1 || items[0].Path != job.PushPath;
                title = $"Pushing {job.PushPath}{(folder ? "/" : string.Empty)} to {job.PushShare}";
                break;
            case JobKind.Resolve:
                items = [.. entries.Where(entry => entry.Status == FileStatus.Conflict).Select(entry => new JobItem(entry.Path, null, "conflict", entry.Size ?? 0, ItemState.Waiting))];
                title = "Resolving conflicts";
                break;
            default:
                items = [.. entries.Select(Item).OfType<JobItem>()];
                title = "Syncing with the share";
                break;
        }

        return new JobView(job, title, null, null, null, items);
    }

    private static JobItem? Item(FolderEntry entry) => entry.Status switch
    {
        FileStatus.NewHere or FileStatus.ChangedHere => new JobItem(entry.Path, TransferDirection.Upload, "upload", entry.Size ?? 0, ItemState.Waiting),
        FileStatus.NewInShare or FileStatus.ChangedInShare => new JobItem(entry.Path, TransferDirection.Download, "download", entry.Size ?? 0, ItemState.Waiting),
        FileStatus.DeletedHere => new JobItem(entry.Path, null, "delete in share", entry.Size ?? 0, ItemState.Waiting),
        FileStatus.DeletedInShare => new JobItem(entry.Path, null, "delete here", entry.Size ?? 0, ItemState.Waiting),
        FileStatus.Conflict => new JobItem(entry.Path, null, "conflict", entry.Size ?? 0, ItemState.Conflict),
        _ => null,
    };

    private static TuiState OnJobFinished(TuiState state, JobFinished finished)
    {
        TuiState done = state with { Running = null, Job = null };
        JobResult result = finished.Result;

        if (result.Status is { } status)
        {
            ImmutableArray<string> before = state.Status?.Warnings ?? [];
            TuiState scanned = WithRows(done with { Status = status, Summary = StatusSummary.Of(status), ScannedAt = state.Now, Problem = null });
            scanned = scanned with { ConflictIndex = Math.Clamp(scanned.ConflictIndex, 0, Math.Max(0, scanned.Summary.ConflictCount - 1)) };
            foreach (string warning in status.Warnings.Where(warning => !before.Contains(warning)))
            {
                scanned = Note(scanned, warning, Role.Conflict);
            }

            string conflicts = scanned.Summary.ConflictCount > 0 ? $", {Count(scanned.Summary.ConflictCount, "conflict")}" : string.Empty;
            int here = status.Entries.Count(entry => entry.Status is FileStatus.NewHere or FileStatus.ChangedHere or FileStatus.DeletedHere);
            int share = status.Entries.Count(entry => entry.Status is FileStatus.NewInShare or FileStatus.ChangedInShare or FileStatus.DeletedInShare);
            return Note(scanned, $"Rescan: {Count(here, "change")} here, {share} in the share{conflicts}.", Role.Muted);
        }

        if (result.Sync is { } sync)
        {
            return Synced(done, finished.Job, sync);
        }

        if (result.Push is { } push)
        {
            return Pushed(done, finished.Job, push);
        }

        return done;
    }

    private static TuiState Synced(TuiState state, SessionJob job, SyncReport report)
    {
        int deleted = report.DeletedLocally + report.DeletedRemotely;
        List<string> parts = [];
        if (job.Kind == JobKind.Resolve)
        {
            parts.Add($"{Count(report.Resolved.Length, "conflict")} resolved");
        }

        parts.Add($"{report.Uploaded.Files} uploaded");
        parts.Add($"{report.Downloaded.Files} downloaded");
        parts.Add($"{deleted} deleted");
        string conflicts = report.Conflicts.Length > 0 ? $", {Count(report.Conflicts.Length, "conflict")} left as it is" : string.Empty;
        string skipped = report.Skipped.Length > 0 ? $", {report.Skipped.Length} skipped" : string.Empty;
        string what = job.Kind == JobKind.Resolve ? "Resolve" : "Sync";

        TuiState next = state with
        {
            Banner = new Banner(
                report.Conflicts.IsEmpty
                    ? string.Create(CultureInfo.InvariantCulture, $"{state.Options.Symbols.Done} {what} finished  {string.Join(" · ", parts)} · the share is at revision {report.Revision}")
                    : string.Create(CultureInfo.InvariantCulture, $"{state.Options.Symbols.Done} {what} finished  {string.Join(" · ", parts)} · revision {report.Revision} · {Count(report.Conflicts.Length, "conflict")} left as it is"),
                report.Conflicts.Length > 0 || report.Skipped.Length > 0 ? Role.Here : Role.Ok),
        };
        next = Note(next, $"{what}: {string.Join(", ", parts)}{conflicts}{skipped}.", Role.Ok);
        foreach (SkippedPath skip in report.Skipped.Take(20))
        {
            next = Note(next, $"Skipped {skip.Path}: {skip.Detail}", Role.Muted);
        }

        foreach (string warning in report.Warnings)
        {
            next = Note(next, warning, Role.Conflict);
        }

        return next;
    }

    private static TuiState Pushed(TuiState state, SessionJob job, PushReport report)
    {
        string share = report.ShareName?.Value ?? job.PushShare ?? "the share";
        string text = report.NothingToPush
            ? $"Nothing to push from {job.PushPath}."
            : $"{state.Options.Symbols.Done} Pushed {Count(report.Files, "file")} ({Text.Bytes(report.Bytes)}) to {share}, into {report.Target}/";
        TuiState next = state with { Banner = new Banner(text, report.Skipped.IsEmpty ? Role.Ok : Role.Here) };
        next = Note(next, $"Push: {job.PushPath} to {share}, {Count(report.Files, "file")} into {report.Target}/.", Role.Ok);
        foreach (SkippedPath skip in report.Skipped.Take(20))
        {
            next = Note(next, $"Not pushed {skip.Path}: {skip.Detail}", Role.Muted);
        }

        foreach (string warning in report.Warnings)
        {
            next = Note(next, warning, Role.Conflict);
        }

        return next;
    }

    private static TuiState OnJobFailed(TuiState state, JobFailed failed)
    {
        TuiState done = state with { Running = null, Job = null };
        Error error = failed.Error;

        if (error.Code == "Sync.MassDeleteBlocked" && state.Status is { } status)
        {
            return Note(done with { Dialog = MassDelete(status) }, error.Description, Role.Conflict);
        }

        if (failed.Job.Kind == JobKind.Rescan)
        {
            done = done with { Problem = error };
        }

        return Note(done with { Banner = new Banner(error.Description, Role.Conflict) }, $"{Capital(failed.Job.Name)} failed: {error.Description}", Role.Conflict);
    }

    private static ConfirmDialog MassDelete(FolderStatus status)
    {
        int here = status.Entries.Count(entry => entry.Status == FileStatus.DeletedInShare);
        int files = status.Entries.Count(entry => entry.ExistsHere && !entry.IsFolder);
        int fromShare = status.Entries.Count(entry => entry.Status == FileStatus.DeletedHere);
        int shared = status.Entries.Count(entry => entry.Status != FileStatus.NewHere && !entry.IsFolder);

        return here >= fromShare
            ? new ConfirmDialog(
                ConfirmKind.MassDelete,
                $"This sync would delete {here} of {files} files here",
                ["The share no longer has them. That is more than half of this folder, so mysync stopped before changing anything."],
                $"Delete {Count(here, "file")} and sync")
            : new ConfirmDialog(
                ConfirmKind.MassDelete,
                $"This sync would remove {fromShare} of {shared} files from the share",
                ["They are gone from this folder. That is more than half of the share, so mysync stopped before changing anything."],
                $"Remove {Count(fromShare, "file")} and sync");
    }

    private static TuiState Refuse(TuiState state, Error error) =>
        Note(state with { Running = null, Banner = new Banner(error.Description, Role.Conflict) }, error.Description, Role.Conflict);

    private static TuiState WithDirection(TuiState state, TransferDirection direction, Func<DirectionProgress, DirectionProgress> change)
    {
        if (state.Job is not { } job)
        {
            return state;
        }

        DirectionProgress current = (direction == TransferDirection.Upload ? job.Upload : job.Download) ?? DirectionProgress.Planned(0, 0);
        DirectionProgress next = change(current);
        return state with { Job = direction == TransferDirection.Upload ? job with { Upload = next } : job with { Download = next } };
    }

    private static TuiState WithItem(TuiState state, string path, ItemState itemState)
    {
        if (state.Job is not { } job)
        {
            return state;
        }

        int index = job.Items.IndexOf(job.Items.FirstOrDefault(item => item.Path == path)!);
        return index < 0 ? state : state with { Job = job with { Items = job.Items.SetItem(index, job.Items[index] with { State = itemState }) } };
    }

    // ----- the diff -----

    private static TuiState OnDiffLoaded(TuiState state, DiffLoaded loaded)
    {
        if (state.Diff is not { } diff || diff.Path != loaded.Path)
        {
            return state;
        }

        if (loaded.Error is not null || loaded.Diff is not { } response || response.Items.IsEmpty)
        {
            return state with { Diff = diff with { Loading = false, Error = loaded.Error?.Description ?? "There is nothing to compare." } };
        }

        return state with { Diff = diff with { Loading = false, Lines = DiffLines(response.Items[0], state.Options.Ascii) } };
    }

    internal static ImmutableArray<DiffLineView> DiffLines(PathDiff item, bool ascii)
    {
        ImmutableArray<DiffLineView>.Builder lines = ImmutableArray.CreateBuilder<DiffLineView>();
        string kind = item.Kind is { } conflict ? $"  {conflict.ToCode()}" : string.Empty;
        lines.Add(new DiffLineView($"{Visible(item.Path.Value, ascii)}{kind}", Role.Text));
        lines.Add(new DiffLineView($"yours {Side(item.Local)}    theirs {Side(item.Remote)}{(item.Remote?.UpdatedBy is { } who ? $" ({who})" : string.Empty)}", Role.Muted));

        foreach (DiffSection section in item.Sections)
        {
            lines.Add(new DiffLineView(string.Empty, Role.Text));
            lines.Add(new DiffLineView($"{section.Title}: {section.OldLabel} → {section.NewLabel}", Role.Muted));
            switch (section.Outcome)
            {
                case TextOutcome text:
                    foreach (DiffHunk hunk in text.Hunks)
                    {
                        lines.Add(new DiffLineView(hunk.Header, Role.Share));
                        foreach (DiffLine line in hunk.Lines)
                        {
                            Role role = line.Kind switch
                            {
                                DiffLineKind.Removed => Role.Conflict,
                                DiffLineKind.Added => Role.Ok,
                                _ => Role.Text,
                            };
                            lines.Add(new DiffLineView(line.Op + Visible(line.Text.TrimEnd('\r'), ascii), role));
                        }
                    }

                    break;
                case IdenticalOutcome:
                    lines.Add(new DiffLineView("Identical.", Role.Muted));
                    break;
                case BinaryOutcome:
                    lines.Add(new DiffLineView("Binary files: not compared line by line.", Role.Muted));
                    break;
                case TooLargeOutcome:
                    lines.Add(new DiffLineView("Too large to compare here.", Role.Muted));
                    break;
                case EolOnlyOutcome eol:
                    lines.Add(new DiffLineView($"Only line endings differ ({eol.Old} → {eol.New}).", Role.Muted));
                    break;
                case ExecOnlyOutcome exec:
                    lines.Add(new DiffLineView($"Only the executable bit differs ({(exec.OldExecutable ? "on" : "off")} → {(exec.NewExecutable ? "on" : "off")}).", Role.Muted));
                    break;
                case TypeMismatchOutcome type:
                    lines.Add(new DiffLineView(Visible(type.Description, ascii), Role.Muted));
                    break;
                default:
                    break;
            }
        }

        foreach (string note in item.Notes)
        {
            lines.Add(new DiffLineView(Visible(note, ascii), Role.Muted));
        }

        return lines.ToImmutable();
    }

    private static string Side(DiffSide? side)
    {
        if (side is null)
        {
            return "deleted";
        }

        return side.Size is { } size ? Text.Bytes(size) : "a folder";
    }

    /// <summary>File content made safe for the screen: control characters become visible placeholders, tabs four spaces.</summary>
    internal static string Visible(string text, bool ascii)
    {
        System.Text.StringBuilder visible = new(text.Length);
        foreach (char c in text)
        {
            if (c == '\t')
            {
                visible.Append("    ");
            }
            else if (c < 0x20)
            {
                visible.Append(ascii ? '?' : (char)(0x2400 + c));
            }
            else if (c == 0x7F)
            {
                visible.Append(ascii ? '?' : '␡');
            }
            else if (c is >= '\u0080' and <= '\u009F')
            {
                visible.Append('?');
            }
            else
            {
                visible.Append(c);
            }
        }

        return visible.ToString();
    }

    // ----- the list -----

    private static TuiState WithFilter(TuiState state, ListFilter filter) => WithRows(state with { Filter = filter }, resetSelection: true);

    /// <summary>Rebuilds the rows after the status, filter, search or collapsed folders changed, keeping the highlight on the same path when it is still there.</summary>
    internal static TuiState WithRows(TuiState state, bool resetSelection = false)
    {
        string? selectedPath = resetSelection ? null : state.SelectedRow?.Path;
        ImmutableArray<ListRow> rows = ListModel.Build(state.Status, state.Filter, state.Search, state.Collapsed);
        int total = state.Filter == ListFilter.All && state.Search.Length == 0
            ? rows.Length
            : ListModel.Build(state.Status, ListFilter.All, string.Empty, state.Collapsed).Length;
        int selected = 0;
        if (selectedPath is not null)
        {
            int found = rows.IndexOf(rows.FirstOrDefault(row => row.Path == selectedPath)!);
            selected = found >= 0 ? found : Math.Min(state.Selected, Math.Max(0, rows.Length - 1));
        }

        return Clamp(state with { Rows = rows, TotalRows = total, Selected = selected });
    }

    private static TuiState Move(TuiState state, int by) => Select(state, state.Selected + by);

    private static TuiState Select(TuiState state, int index) => Clamp(state with { Selected = index });

    /// <summary>The highlight inside the list, and the list scrolled so the highlight is visible.</summary>
    internal static TuiState Clamp(TuiState state)
    {
        int selected = Math.Clamp(state.Selected, 0, Math.Max(0, state.Rows.Length - 1));
        int height = state.ListHeight;
        int scroll = state.Scroll;
        if (selected < scroll)
        {
            scroll = selected;
        }
        else if (selected >= scroll + height)
        {
            scroll = selected - height + 1;
        }

        scroll = Math.Clamp(scroll, 0, Math.Max(0, state.Rows.Length - height));
        return state with { Selected = selected, Scroll = scroll };
    }

    private static TuiState Expand(TuiState state) =>
        state.SelectedRow is { IsFolder: true, Expanded: false } row ? WithRows(state with { Collapsed = state.Collapsed.Remove(row.Path) }) : state;

    private static TuiState CollapseOrUp(TuiState state)
    {
        if (state.SelectedRow is not { } row)
        {
            return state;
        }

        if (row.IsFolder && row.Expanded)
        {
            return WithRows(state with { Collapsed = state.Collapsed.Add(row.Path) });
        }

        int slash = row.Path.LastIndexOf('/');
        if (slash < 0)
        {
            return state;
        }

        string parent = row.Path[..slash];
        int index = state.Rows.IndexOf(state.Rows.FirstOrDefault(candidate => candidate.Path == parent && candidate.IsFolder)!);
        return index < 0 ? state : Select(state, index);
    }

    // ----- helpers -----

    internal static TuiState Note(TuiState state, string text, Role role)
    {
        ImmutableList<ActivityLine> activity = state.Activity.Insert(0, new ActivityLine(state.Now, text, role));
        return state with { Activity = activity.Count > MaxActivity ? activity.RemoveRange(MaxActivity, activity.Count - MaxActivity) : activity };
    }

    internal static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string DropLast(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        int cut = text.Length >= 2 && char.IsLowSurrogate(text[^1]) ? 2 : 1;
        return text[..^cut];
    }

    /// <summary>Where a push of <paramref name="item"/> lands in the other share, as the dialog previews it.</summary>
    internal static string LandsIn(TuiState state, string item, bool isFolder)
    {
        Result<PushTarget> target = PushTarget.Create(state.Options.HostName, state.Now);
        string name = item.Contains('/', StringComparison.Ordinal) ? item[(item.LastIndexOf('/') + 1)..] : item;
        string folder = target.IsSuccess ? target.Value.Candidate(1).Value : "<host>/<time>";
        return $"{folder}/{name}{(isFolder ? "/" : string.Empty)}";
    }
}
