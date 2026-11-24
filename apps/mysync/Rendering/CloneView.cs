using MySync.Application.Shares.CloneInto;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync clone</c>: the clone summary with the folder, the <c>cd</c> hint, and what a failed clone's clean-up left behind.</summary>
internal static class CloneView
{
    public static void Render(CloneIntoFolderResponse cloned, string current)
    {
        ArgumentNullException.ThrowIfNull(cloned);

        (string shown, string? cd) = CloneLocation.Of(cloned.Folder, current, OperatingSystem.IsWindows());
        SyncReportView.Render(cloned.Report, cloned: true, into: shown);
        if (cd is not null)
        {
            AnsiConsole.MarkupLine($"Next:  [bold]{ConsoleOutput.Value($"cd {cd}")}[/]");
        }
    }

    /// <summary>After a failure or Ctrl+C, on stderr: what was removed, and what is still there.</summary>
    public static void RenderCleanUp(CloneCleanUpReport? cleanUp)
    {
        if (cleanUp is null)
        {
            return;
        }

        if (cleanUp.LeftBehind.Count == 0)
        {
            ConsoleOutput.Info(cleanUp.FolderRemoved
                ? $"Removed '{cleanUp.Folder}' again: nothing of the clone is left."
                : $"Removed what the clone wrote; '{cleanUp.Folder}' is as it was.");
            return;
        }

        ConsoleOutput.Warn($"Left in '{cleanUp.Folder}' (not written by this clone, or couldn't be removed):");
        foreach (string left in cleanUp.LeftBehind)
        {
            ConsoleOutput.Warn($"  {left}");
        }
    }
}
