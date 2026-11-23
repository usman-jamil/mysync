using System.Globalization;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Session;

/// <summary>Why the folder session (<c>mysync tui</c>) didn't start a job (CLAUDE.md §10d).</summary>
public static class SessionErrors
{
    /// <summary>One job at a time.</summary>
    public static Error Busy(string job) => Error.Conflict(
        "Session.Busy",
        $"Wait for the {job} to finish, or cancel it.");

    /// <summary>Pushing into the share this folder syncs with: that is what Sync is for.</summary>
    public static Error PushToOwnShare(string share) => Error.Validation(
        "Session.PushToOwnShare",
        $"This folder already syncs with {share}. Choose another share, or use Sync.");

    /// <summary>The item only exists in the share, or was deleted here.</summary>
    public static Error NothingOnDisk(string path) => Error.Validation(
        "Session.NothingOnDisk",
        $"Nothing on disk to push for '{PathErrors.Display(path)}'.");
}

/// <summary>Why <c>mysync tui</c> didn't open its screen (CLAUDE.md §10d).</summary>
public static class TuiErrors
{
    /// <summary>stdin or stdout is redirected. Exit 2.</summary>
    public static Error NotATerminal() => Error.Validation(
        "Tui.NotATerminal",
        "mysync tui needs an interactive terminal. Use mysync sync in scripts.");

    /// <summary>The window is smaller than 80 × 24. Exit 2.</summary>
    public static Error TooSmall(int width, int height) => Error.Validation(
        "Tui.TooSmall",
        string.Create(CultureInfo.InvariantCulture, $"mysync tui needs a window of at least 80 × 24; this one is {width} × {height}."));

    /// <summary>Another <c>mysync tui</c> holds <c>.mysynctool.ui</c> in this folder. Exit 6.</summary>
    public static Error AlreadyRunning(int? processId) => Error.Conflict(
        "Tui.AlreadyRunning",
        processId is { } pid
            ? string.Create(CultureInfo.InvariantCulture, $"mysync tui is already running in this folder (process {pid}).")
            : "mysync tui is already running in this folder.");
}
