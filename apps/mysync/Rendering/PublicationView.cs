using MySync.Application.Abstractions.Publishing;
using MySync.Application.Publishing.List;
using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;
using MySync.Domain.Paths;
using MySync.Domain.Publishing;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>publish</c>, <c>publications</c> and <c>unpublish</c> on screen. Titles and paths are user-supplied, so always escaped.</summary>
internal static class PublicationView
{
    public static void Render(PublishReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string warning in report.Warnings)
        {
            ConsoleOutput.Warn(warning);
        }

        foreach (UnpublishablePath skipped in report.Skipped)
        {
            ConsoleOutput.Warn($"Not published: {PathErrors.Display(skipped.Path.Value)} ({skipped.Reason}).");
        }

        string files = report.Files == 1 ? "file" : "files";
        AnsiConsole.MarkupLine(
            $"[green]Published[/] {ConsoleOutput.Value(report.Title)}: revision {report.Revision}, {report.Files} {files} ({ConsoleOutput.Bytes(report.Bytes)})");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(report.Url.AbsoluteUri)}");
        AnsiConsole.MarkupLine(ConsoleOutput.Muted("Anyone with this link can browse and download these files. Remove it with:"));
        AnsiConsole.MarkupLine($"  mysync unpublish {ConsoleOutput.Value(report.PublishId.Value)}");
        AnsiConsole.MarkupLine(ConsoleOutput.Muted("Download everything:"));
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(report.ShellCommand)}");
        AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(report.PowerShellCommand)}");

        if (report.Copies.TryGetValue(CopyMethod.Streamed, out int streamed) && streamed > 0)
        {
            ConsoleOutput.Info($"{streamed} {(streamed == 1 ? "file was" : "files were")} copied through this machine: the private connection string can't sign a read-only URL for a server-side copy (use the account key to avoid this).");
        }

        if (!report.Verified)
        {
            ConsoleOutput.Warn(
                $"The publication is up, but it couldn't be opened anonymously at {report.Url.AbsoluteUri}{PublicationLayout.SiteData} ({report.VerificationDetail}). " +
                "Check that MYSYNC_PUBLISH_BASE_URL is the static website endpoint of the publishing account.");
        }
    }

    public static void Render(PublicationListResponse listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        if (listed.Publications.IsEmpty)
        {
            AnsiConsole.MarkupLine($"Share {ConsoleOutput.Value(listed.ShareId.ToString())} has no publications.");
            return;
        }

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        foreach (string column in (string[])["ID", "Status", "Title", "Revision", "Files", "Size", "Created", "URL"])
        {
            table.AddColumn(column);
        }

        foreach (Publication publication in listed.Publications)
        {
            string status = publication.Status switch
            {
                PublicationStatus.Published => "[green]published[/]",
                PublicationStatus.Pending => "[yellow]pending[/]",
                _ => "[red]failed[/]",
            };
            table.AddRow(
                ConsoleOutput.Value(publication.Id.Value),
                status,
                ConsoleOutput.Value(publication.Title),
                publication.Revision.ToString(),
                publication.Files.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ConsoleOutput.Bytes(publication.Bytes),
                ConsoleOutput.Value(publication.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
                ConsoleOutput.Value(publication.Url));
        }

        AnsiConsole.Write(table);
        if (listed.Publications.Any(publication => publication.Status != PublicationStatus.Published))
        {
            AnsiConsole.MarkupLine(ConsoleOutput.Muted("A pending or failed publication may have left files behind; `mysync unpublish <id>` removes them."));
        }
    }

    public static void Render(UnpublishReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        AnsiConsole.MarkupLine($"[green]Unpublished[/] {ConsoleOutput.Value(report.PublishId.Value)}: {ConsoleOutput.Value(report.Url)} no longer works ({report.BlobsDeleted} files removed).");
    }
}
