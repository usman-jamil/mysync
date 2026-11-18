using System.Globalization;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync info</c>: the share, where it lives, and how this folder compares with HEAD.</summary>
internal static class ShareInfoView
{
    public static void Render(ShareInfoResponse info)
    {
        ArgumentNullException.ThrowIfNull(info);

        long behind = info.RemoteRevision.Value - info.BaseRevision.Value;
        string revisions = behind == 0
            ? $"{info.BaseRevision.Value} (up to date with the share)"
            : $"{info.BaseRevision.Value} here, {info.RemoteRevision.Value} in the share ({behind} behind; run mysync sync)";

        Grid fields = ConsoleOutput.Fields();
        Add(fields, "Share", info.ShareId.ToString());
        Add(fields, "Folder", info.RootPath);
        Add(fields, "Storage", $"{info.Location.Provider}: {info.Location.AccountName} / {info.Location.Container}");
        Add(fields, "Revision", revisions);
        Add(fields, "Last change", $"{info.RemoteUpdatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)} by {Domain.Paths.PathErrors.Display(info.RemoteUpdatedBy)}");
        Add(fields, "Created", info.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Add(fields, "Files", $"{info.FileCount} ({ConsoleOutput.Bytes(info.TotalBytes)})");
        Add(fields, "Conflicts", info.OpenConflicts == 0 ? "none" : $"{info.OpenConflicts} open (mysync conflicts)");
        ConsoleOutput.Write(fields);
    }

    /// <summary><c>mysync delete</c>: what is gone, what was kept here, and what the storage account may still hold.</summary>
    public static void Render(DeleteShareResponse deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);

        foreach (string warning in deleted.Warnings)
        {
            ConsoleOutput.Warn(warning);
        }

        if (deleted.PublicationsRemoved > 0)
        {
            AnsiConsole.MarkupLine($"Took down {deleted.PublicationsRemoved} {(deleted.PublicationsRemoved == 1 ? "publication" : "publications")} first: {(deleted.PublicationsRemoved == 1 ? "its link no longer works" : "their links no longer work")}.");
        }

        string files = deleted.ObjectsDeleted == 1 ? "file" : "files";
        AnsiConsole.MarkupLine(deleted.WasAlreadyDeleted
            ? $"[green]Share {ConsoleOutput.Value(deleted.ShareId.ToString())} was already deleted[/] by {ConsoleOutput.Value(Domain.Paths.PathErrors.Display(deleted.Deletion.DeletedBy))}; finished removing what was left."
            : $"[green]Deleted share {ConsoleOutput.Value(deleted.ShareId.ToString())}[/]: {deleted.ObjectsDeleted} stored {files} ({ConsoleOutput.Bytes(deleted.BytesDeleted)}) and its history are gone.");
        AnsiConsole.MarkupLine(ConsoleOutput.Muted("Other machines are told the share was deleted the next time they sync; their files are left alone."));

        if (deleted.LocalStateRemoved)
        {
            AnsiConsole.MarkupLine(ConsoleOutput.Muted("This folder's files were kept; it is an ordinary folder again (.mysynctool and its log were removed)."));
        }

        if (deleted.RecoverableCopies > 0)
        {
            ConsoleOutput.Warn($"The storage account still keeps {deleted.RecoverableCopies} deleted {(deleted.RecoverableCopies == 1 ? "copy" : "copies")} (soft delete or blob versioning). They can be restored until the account's retention period ends; purge them in Azure if the data must be gone now.");
        }
    }

    private static void Add(Grid fields, string label, string value) => fields.AddRow(ConsoleOutput.Label(label), ConsoleOutput.Value(value));
}
