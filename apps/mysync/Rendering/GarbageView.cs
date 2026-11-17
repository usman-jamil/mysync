using MySync.Application.Maintenance.CollectGarbage;
using MySync.Domain.Storage;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync gc</c>: always says exactly what was (or, with <c>--dry-run</c>, would be) deleted.</summary>
internal static class GarbageView
{
    public static void Render(GarbageReport report, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(report);
        string verb = report.DryRun ? "Would delete" : "Deleted";

        if (report.DeletedObjects.IsEmpty && report.DeletedHistory.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[green]Nothing to collect[/] {ConsoleOutput.Muted($"(revision {report.Head.Value}, {report.KeptObjects} objects in use)")}");
        }
        else
        {
            AnsiConsole.MarkupLine($"{(report.DryRun ? "[bold]Dry run[/] — nothing was deleted. " : "[green]Done.[/] ")}{verb} {report.DeletedObjects.Length} unused {(report.DeletedObjects.Length == 1 ? "object" : "objects")} ({ConsoleOutput.Bytes(report.DeletedBytes)}) and {report.DeletedHistory.Length} old history {(report.DeletedHistory.Length == 1 ? "manifest" : "manifests")}.");
            var layout = ShareLayout.For(report.ShareId);
            foreach (Application.Abstractions.Storage.StoredObjectInfo item in verbose ? report.DeletedObjects : report.DeletedObjects.Take(20))
            {
                AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(layout.ObjectBlob(item.Hash))}  {ConsoleOutput.Muted(ConsoleOutput.Bytes(item.Size))}");
            }

            if (!verbose && report.DeletedObjects.Length > 20)
            {
                AnsiConsole.MarkupLine(ConsoleOutput.Muted($"  … and {report.DeletedObjects.Length - 20} more (--verbose lists them all)"));
            }

            foreach (Domain.Manifests.Revision revision in report.DeletedHistory)
            {
                AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(layout.ManifestHistory(revision))}");
            }
        }

        AnsiConsole.MarkupLine(ConsoleOutput.Muted(
            $"Kept: revisions {string.Join(", ", report.KeptRevisions.Select(revision => revision.Value))}; {report.KeptObjects} objects in use; {report.ProtectedByGrace} unused but too recent to delete."));
    }
}
