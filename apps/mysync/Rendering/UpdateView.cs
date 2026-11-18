using MySync.Application.Maintenance.Update;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync update</c>: what's installed, what's available, and what changed.</summary>
internal static class UpdateView
{
    public static void Render(UpdateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        switch (report.Status)
        {
            case UpdateStatus.UpToDate:
                AnsiConsole.MarkupLine($"[green]mysync {ConsoleOutput.Value(report.Current.ToString())} is up to date[/] {ConsoleOutput.Muted($"(from {report.Source})")}");
                return;

            case UpdateStatus.Available:
                AnsiConsole.MarkupLine(report.Downgrade
                    ? $"mysync {ConsoleOutput.Value(report.Target.ToString())} is available (older than this {ConsoleOutput.Value(report.Current.ToString())}). Run [bold]mysync update --version {ConsoleOutput.Value(report.Target.ToString())}[/] to install it."
                    : $"[bold]mysync {ConsoleOutput.Value(report.Target.ToString())} is available[/] (this is {ConsoleOutput.Value(report.Current.ToString())}). Run [bold]mysync update[/] to install it.");
                break;

            default:
                AnsiConsole.MarkupLine($"[green]Updated mysync {ConsoleOutput.Value(report.Current.ToString())} → {ConsoleOutput.Value(report.Target.ToString())}[/] {ConsoleOutput.Muted($"({report.ExecutablePath})")}");
                AnsiConsole.MarkupLine(ConsoleOutput.Muted("Verified against the release signature and its own self-check. The new version runs from the next command."));
                break;
        }

        if (report.SignificantChange)
        {
            ConsoleOutput.Warn($"{report.Current} → {report.Target} is a significant change: read the release notes, and update every machine that shares folders with this one.");
        }

        if (report.NotesUrl is { } notes)
        {
            AnsiConsole.MarkupLine($"Release notes: {ConsoleOutput.Value(notes)}");
        }
    }
}
