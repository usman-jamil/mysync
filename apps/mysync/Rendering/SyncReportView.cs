using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary>
/// The end-of-run summary for people (CLAUDE.md §10): what moved, then every conflict and every
/// skipped path with its reason and what to do next. Output goes to stdout; warnings to stderr.
/// Every path is user data, so every path is escaped.
/// </summary>
internal static class SyncReportView
{
    private const int HashLength = 8;

    /// <param name="into">For <c>mysync clone</c>: the folder cloned into, as shown.</param>
    public static void Render(SyncReport report, bool cloned, string? into = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        RenderWarnings(report.Warnings);
        AnsiConsole.MarkupLine(Totals(report));

        AnsiConsole.MarkupLine(Outcome(report, cloned, into));

        RenderConflicts(report.Conflicts);
        RenderSkipped(report.Skipped);
    }

    public static void Render(CreateShareResponse created)
    {
        ArgumentNullException.ThrowIfNull(created);

        RenderWarnings(created.Warnings);
        string sent = created.Compression == CompressionSetting.Gzip ? " (compression on)" : string.Empty;
        if (created.Uploaded.Compressed)
        {
            sent = $" (sent {ConsoleOutput.Bytes(created.Uploaded.StoredBytes)}, compressed)";
        }
        AnsiConsole.MarkupLine(
            $"[green]Created share {ConsoleOutput.Value(created.Name.Value)}[/] ({ConsoleOutput.Value(created.ShareId.ToString())}): {created.FileCount} {Files(created.FileCount)}, {ConsoleOutput.Bytes(created.TotalBytes)}{sent}");
        AnsiConsole.MarkupLine($"Share it with:  [bold]{ConsoleOutput.Value($"mysync clone {created.Name}")}[/]");
        RenderSkipped(created.Skipped);
    }

    /// <summary><c>mysync resolve</c>: what was settled, and what is still open because the remote moved on.</summary>
    public static void RenderResolve(SyncReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        RenderWarnings(report.Warnings);
        string verb = report.DryRun ? "Would resolve" : "Resolved";
        if (report.Resolved.IsEmpty && report.Conflicts.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]No open conflicts[/] — nothing to resolve.");
        }
        else if (!report.Resolved.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[green]{verb} {report.Resolved.Length} {(report.Resolved.Length == 1 ? "conflict" : "conflicts")}[/]{(report.DryRun ? " (dry run — nothing was changed)" : string.Empty)}:");
            foreach (Domain.Paths.SharePath path in report.Resolved)
            {
                AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(PathErrors.Display(path.Value))}");
            }
        }

        if (!report.DryRun && !report.Resolved.IsEmpty)
        {
            AnsiConsole.MarkupLine(Totals(report));
        }

        if (!report.Conflicts.IsEmpty)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Still in conflict[/] — the remote changed again since the conflict was recorded, so nothing was forced:");
        }

        RenderConflicts(report.Conflicts);
        RenderSkipped(report.Skipped);
    }

    public static void RenderConflicts(IReadOnlyCollection<Conflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]{conflicts.Count} {(conflicts.Count == 1 ? "conflict" : "conflicts")}[/] — both sides changed these; neither change was applied:");
        Table table = new Table().Border(TableBorder.Simple)
            .AddColumn("Path").AddColumn("Kind").AddColumn("Local").AddColumn("Remote").AddColumn("Remote changed by");
        foreach (Conflict conflict in conflicts)
        {
            table.AddRow(
                ConsoleOutput.Value(PathErrors.Display(conflict.Path.Value)),
                ConsoleOutput.Value(conflict.Kind.ToCode()),
                ConsoleOutput.Value(Side(conflict.Local)),
                ConsoleOutput.Value(Side(conflict.Remote)),
                ConsoleOutput.Value($"{PathErrors.Display(conflict.RemoteUpdatedBy)} (rev {conflict.RemoteRevision.Value})"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(ConsoleOutput.Muted("See what differs:  mysync diff            (or mysync diff <path>)"));
        AnsiConsole.MarkupLine(ConsoleOutput.Muted("Decide with:       mysync resolve <path> --keep local|remote   (or --all)"));
    }

    public static void RenderSkipped(IReadOnlyCollection<SkippedPath> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        if (skipped.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{skipped.Count} {(skipped.Count == 1 ? "path" : "paths")} skipped[/] — left as they are and retried next run:");
        foreach (SkippedPath skip in skipped)
        {
            AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(PathErrors.Display(skip.Path))}  {ConsoleOutput.Muted($"{JsonOutput.CauseCode(skip.Cause)}: {skip.Detail}")}");
        }
    }

    private static void RenderWarnings(IEnumerable<string> warnings)
    {
        foreach (string warning in warnings)
        {
            ConsoleOutput.Warn(warning);
        }
    }

    private static string Outcome(SyncReport report, bool cloned, string? into)
    {
        string revision = $"revision {report.Revision.Value}";
        if (cloned)
        {
            string which = report.Name is { } name ? $"{name} ({report.ShareId})" : report.ShareId.ToString();
            string where = into is null ? string.Empty : $" into {ConsoleOutput.Value(into)}";
            return $"[green]Cloned share {ConsoleOutput.Value(which)}[/]{where} {ConsoleOutput.Muted($"({revision})")}";
        }

        if (report.Committed)
        {
            return $"[green]Synced[/] {ConsoleOutput.Muted($"(committed {revision})")}";
        }

        bool changedHere = report.Downloaded.Files + report.DeletedLocally > 0;
        return $"[green]{(changedHere ? "Synced" : "Already up to date")}[/] {ConsoleOutput.Muted($"(at {revision})")}";
    }

    private static string Totals(SyncReport report)
    {
        int deleted = report.DeletedLocally + report.DeletedRemotely;
        string sent = report.Uploaded.Compressed ? $" (sent {ConsoleOutput.Bytes(report.Uploaded.StoredBytes)})" : string.Empty;
        return $"↑ {report.Uploaded.Files} {Files(report.Uploaded.Files)} {ConsoleOutput.Bytes(report.Uploaded.Bytes)}{sent}  "
            + $"↓ {report.Downloaded.Files} {Files(report.Downloaded.Files)} {ConsoleOutput.Bytes(report.Downloaded.Bytes)}  "
            + $"✗ {deleted} deleted";
    }

    private static string Side(ManifestEntry? entry) => entry switch
    {
        null => "deleted",
        DirectoryEntry => "directory",
        FileEntry file => $"{file.Sha256.Value[..HashLength]} {ConsoleOutput.Bytes(file.Size)} {file.MtimeUtc.UtcDateTime:yyyy-MM-dd HH:mm}",
        _ => "?",
    };

    private static string Files(int count) => count == 1 ? "file" : "files";
}
