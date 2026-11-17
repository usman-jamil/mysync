using MySync.Application.Shares.Preview;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>--dry-run</c> (and later <c>status</c>): what a sync would do, path by path, without doing it.</summary>
internal static class PlanView
{
    /// <summary>Paths shown per kind of change before "… and N more" (all of them with <c>--verbose</c>).</summary>
    private const int PathsPerKind = 20;

    public static void Render(SyncReport report, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<IGrouping<string, SyncAction>> groups = [.. report.Plan.Actions
            .Where(action => action.Kind is not (SyncActionKind.NoChange or SyncActionKind.Conflict or SyncActionKind.Skip))
            .GroupBy(action => Describe(action.Kind))];

        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to do[/] — this folder and the share agree.");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]Dry run[/] — nothing was changed. A sync would:");
            foreach (IGrouping<string, SyncAction> group in groups)
            {
                List<SyncAction> actions = [.. group];
                AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(group.Key)} ({actions.Count})");
                foreach (SyncAction action in verbose ? actions : actions.Take(PathsPerKind))
                {
                    AnsiConsole.MarkupLine($"      {ConsoleOutput.Value(PathErrors.Display(action.Path.Value))}");
                }

                if (!verbose && actions.Count > PathsPerKind)
                {
                    AnsiConsole.MarkupLine(ConsoleOutput.Muted($"      … and {actions.Count - PathsPerKind} more (--verbose lists them all)"));
                }
            }
        }

        SyncReportView.RenderConflicts(report.Conflicts);
        SyncReportView.RenderSkipped(report.Skipped);
    }

    public static void Render(NewSharePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        AnsiConsole.MarkupLine(
            $"[bold]Dry run[/] — this folder is not a share yet. A sync would create one from {preview.FileCount} {(preview.FileCount == 1 ? "file" : "files")} ({ConsoleOutput.Bytes(preview.TotalBytes)}) in {ConsoleOutput.Value(preview.RootPath)}.");
    }

    private static string Describe(SyncActionKind kind) => kind switch
    {
        SyncActionKind.Upload => "↑ upload",
        SyncActionKind.UpdateRemoteMode => "↑ change the executable bit in the share",
        SyncActionKind.AddRemoteDirectory => "↑ add an empty directory to the share",
        SyncActionKind.RemoveRemote => "✗ delete from the share",
        SyncActionKind.Download => "↓ download",
        SyncActionKind.UpdateLocalMode => "↓ change the executable bit here",
        SyncActionKind.CreateLocalDirectory => "↓ create an empty directory here",
        SyncActionKind.DeleteLocal => "✗ delete here",
        _ => kind.ToString(),
    };
}
