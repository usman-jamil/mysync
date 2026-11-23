using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using System.Globalization;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;
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
            AnsiConsole.MarkupLine(report.Filters is null
                ? "Nothing to push."
                : $"Nothing to push ({LeftOut(report.FilteredOut.Total)} by filters).");
            RenderFilters(report);
            return;
        }

        if (report.DryRun)
        {
            string create = report.Created ? " (the share would be created)" : string.Empty;
            AnsiConsole.MarkupLine(
                $"Would push {Files(report.Files)} ({ConsoleOutput.Bytes(report.Bytes)}) to {ConsoleOutput.Value(share)}, folder {ConsoleOutput.Value(report.Target)}/{ConsoleOutput.Value(create)} — nothing was changed.");
            AnsiConsole.MarkupLine(ConsoleOutput.Muted($"  {report.Uploaded.Files} to upload ({ConsoleOutput.Bytes(report.Uploaded.Bytes)}), {report.AlreadyStored.Files} already stored"));
            RenderFilters(report);

            // With filters, which files made it through is the point of a dry run.
            if (report.Filters is not null)
            {
                foreach (string path in report.Planned)
                {
                    AnsiConsole.MarkupLine($"    {ConsoleOutput.Value(PathErrors.Display(path))}");
                }
            }

            return;
        }

        string sent = report.Uploaded.Compressed ? $", sent {ConsoleOutput.Bytes(report.Uploaded.StoredBytes)}" : string.Empty;
        string counts = $"{Files(report.Files)} ({ConsoleOutput.Bytes(report.Bytes)}; {report.Uploaded.Files} uploaded, {report.AlreadyStored.Files} already stored{sent})";
        AnsiConsole.MarkupLine(report.Created
            ? $"[green]Created share {ConsoleOutput.Value(share)}[/] and pushed {ConsoleOutput.Value(counts)}"
            : $"[green]Pushed[/] {ConsoleOutput.Value(counts)} to {ConsoleOutput.Value(share)}");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Label("folder: ")} {ConsoleOutput.Value(report.Target)}/");
        RenderFilters(report);
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

    /// <summary><c>changed since 2026-11-22 23:45 UTC · 203 files left out by filters (188 not changed, 14 excluded, 1 too large)</c>; nothing without filters.</summary>
    private static void RenderFilters(PushReport report)
    {
        if (report.Filters is not { } filters)
        {
            return;
        }

        List<string> parts = [];
        if (filters.UpdatedSinceSpec is not null)
        {
            parts.Add(filters.UpdatedSinceUtc is { } cutoff
                ? string.Create(CultureInfo.InvariantCulture, $"changed since {cutoff.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
                : $"no earlier push from {HostOf(report.Target)}; pushing everything");
        }

        PushFilteredOut counts = report.FilteredOut;
        string[] reasons = [.. new (int Count, string Label)[]
            {
                (counts.NotChanged, "not changed"),
                (counts.NotIncluded, "not included"),
                (counts.Excluded, "excluded"),
                (counts.TooLarge, "too large"),
            }
            .Where(reason => reason.Count > 0)
            .Select(reason => $"{reason.Count} {reason.Label}")];
        parts.Add(counts.Total == 0 ? "no files left out by filters" : $"{LeftOut(counts.Total)} by filters ({string.Join(", ", reasons)})");

        AnsiConsole.MarkupLine($"  {ConsoleOutput.Muted(string.Join(" · ", parts))}");
    }

    private static string LeftOut(int count) => count == 1 ? "1 file left out" : $"{count} files left out";

    private static string HostOf(string target) => target.Split('/')[0];

    private static string ShareLabel(PushReport report) => report.ShareName?.Value ?? report.ShareId?.ToString() ?? "the share";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
