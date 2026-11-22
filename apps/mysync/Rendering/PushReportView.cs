using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync push</c> on screen: what went where, and how to get it. Paths and names are user-supplied, so always escaped.</summary>
internal static class PushReportView
{
    public static void Render(PushReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string warning in report.Warnings)
        {
            ConsoleOutput.Warn(warning);
        }

        RenderSkipped(report.Skipped);
        string share = ShareLabel(report);

        if (report.NothingToPush)
        {
            AnsiConsole.MarkupLine("Nothing to push.");
            return;
        }

        if (report.DryRun)
        {
            string create = report.Created ? " (the share would be created)" : string.Empty;
            AnsiConsole.MarkupLine(
                $"Would push {Files(report.Files)} ({ConsoleOutput.Bytes(report.Bytes)}) to {ConsoleOutput.Value(share)}, folder {ConsoleOutput.Value(report.Target)}/{ConsoleOutput.Value(create)} — nothing was changed.");
            AnsiConsole.MarkupLine(ConsoleOutput.Muted($"  {report.Uploaded.Files} to upload ({ConsoleOutput.Bytes(report.Uploaded.Bytes)}), {report.AlreadyStored.Files} already stored"));
            return;
        }

        string sent = report.Uploaded.Compressed ? $", sent {ConsoleOutput.Bytes(report.Uploaded.StoredBytes)}" : string.Empty;
        string counts = $"{Files(report.Files)} ({ConsoleOutput.Bytes(report.Bytes)}; {report.Uploaded.Files} uploaded, {report.AlreadyStored.Files} already stored{sent})";
        AnsiConsole.MarkupLine(report.Created
            ? $"[green]Created share {ConsoleOutput.Value(share)}[/] and pushed {ConsoleOutput.Value(counts)}"
            : $"[green]Pushed[/] {ConsoleOutput.Value(counts)} to {ConsoleOutput.Value(share)}");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Label("folder: ")} {ConsoleOutput.Value(report.Target)}/");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Label("revision")} {report.Revision}");
        AnsiConsole.MarkupLine(
            $"Get them with:  {ConsoleOutput.Value($"mysync sync {share}")}      {ConsoleOutput.Muted("(or mysync sync, in a folder already cloned from it)")}");
    }

    private static void RenderSkipped(IReadOnlyCollection<SkippedPath> skipped)
    {
        if (skipped.Count == 0)
        {
            return;
        }

        ConsoleOutput.Warn($"{skipped.Count} {(skipped.Count == 1 ? "path was" : "paths were")} not pushed:");
        foreach (SkippedPath skip in skipped)
        {
            ConsoleOutput.Errors.MarkupLine($"  {ConsoleOutput.Value(PathErrors.Display(skip.Path))}  {ConsoleOutput.Muted($"{JsonOutput.CauseCode(skip.Cause)}: {skip.Detail}")}");
        }
    }

    private static string ShareLabel(PushReport report) => report.ShareName?.Value ?? report.ShareId?.ToString() ?? "the share";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
