using MySync.Application.Sync.Pull;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Pull;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync pull</c> on screen: what came from where, and where it went. Paths are user- and share-supplied, so always escaped.</summary>
internal static class PullReportView
{
    public static void Render(PullReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string warning in report.Warnings)
        {
            ConsoleOutput.Warn(warning);
        }

        RenderSkipped(report.Skipped);
        string share = report.ShareName?.Value ?? report.ShareId.ToString();
        string revision = $"revision {report.Revision}";
        if (report.Latest)
        {
            revision += report.Selected.Count == 1 ? " (the newest push of 1 host)" : $" (the newest push of {report.Selected.Count} hosts)";
        }

        if (report.NothingToPull)
        {
            AnsiConsole.MarkupLine($"Nothing to pull from {ConsoleOutput.Value(share)} ({ConsoleOutput.Value(revision)}).");
            return;
        }

        string counts = $"{Files(report.Files)} ({ConsoleOutput.Bytes(report.Bytes)})";
        AnsiConsole.MarkupLine(report.DryRun
            ? $"Would pull {ConsoleOutput.Value(counts)} from {ConsoleOutput.Value(share)}, {ConsoleOutput.Value(revision)} — nothing was written."
            : $"[green]Pulled[/] {ConsoleOutput.Value(counts)} from {ConsoleOutput.Value(share)}, {ConsoleOutput.Value(revision)}");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Label("into:  ")} {ConsoleOutput.Value(Into(report))}");

        List<string> details = [];
        if (report.AlreadyThere.Files > 0)
        {
            details.Add($"{(report.DryRun ? "would download" : "downloaded")} {report.Downloaded.Files}, {report.AlreadyThere.Files} already there");
        }

        if (report.Overwritten > 0)
        {
            details.Add($"{report.Overwritten} {(report.DryRun ? "would be replaced" : "replaced")} (--overwrite)");
        }

        if (details.Count > 0)
        {
            AnsiConsole.MarkupLine($"  {ConsoleOutput.Muted(string.Join(" · ", details))}");
        }
    }

    // ./20261123-004512Z/, ./web01/20261123-004512Z/, …: where each selected path landed, from the current folder when it's below it.
    private static string Into(PullReport report) =>
        string.Join(", ", report.Selected.Select(selected => PathErrors.Display(Shown(report.Destination, selected))));

    private static string Shown(string destination, PullRoot selected)
    {
        string full = selected.LocalPath.Length == 0
            ? destination
            : Path.Combine(destination, selected.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, full);
        bool below = !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
        string shown = below ? Below(relative) : full;
        return selected.IsFile ? shown : shown + Path.DirectorySeparatorChar;
    }

    private static string Below(string relative) => relative == "." ? "." : $".{Path.DirectorySeparatorChar}{relative}";

    private static void RenderSkipped(IReadOnlyCollection<SkippedPath> skipped)
    {
        if (skipped.Count == 0)
        {
            return;
        }

        ConsoleOutput.Warn($"{skipped.Count} {(skipped.Count == 1 ? "path was" : "paths were")} not pulled:");
        foreach (SkippedPath skip in skipped)
        {
            ConsoleOutput.Errors.MarkupLine($"  {ConsoleOutput.Value(PathErrors.Display(skip.Path))}  {ConsoleOutput.Muted($"{JsonOutput.CauseCode(skip.Cause)}: {skip.Detail}")}");
        }
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
