using System.Globalization;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.List;
using MySync.Application.Shares.Rename;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary><c>mysync info</c>: the share, where it lives, and how this folder compares with HEAD.</summary>
internal static class ShareInfoView
{
    public static void Render(ShareInfoResponse info)
    {
        ArgumentNullException.ThrowIfNull(info);

        string revisions;
        if (info.BaseRevision is { } baseRevision)
        {
            long behind = info.RemoteRevision.Value - baseRevision.Value;
            revisions = behind == 0
                ? $"{baseRevision.Value} (up to date with the share)"
                : $"{baseRevision.Value} here, {info.RemoteRevision.Value} in the share ({behind} behind; run mysync sync)";
        }
        else
        {
            revisions = $"{info.RemoteRevision.Value} in the share";
        }

        Grid fields = ConsoleOutput.Fields();
        Add(fields, "Name", info.Names.Count == 0 ? "— (name it with mysync rename <name>)" : string.Join(", ", info.Names.Select(name => name.Value)));
        Add(fields, "Share", info.ShareId.ToString());
        if (info.RootPath is { } rootPath)
        {
            Add(fields, "Folder", rootPath);
        }

        Add(fields, "Storage", $"{info.Location.Provider}: {info.Location.AccountName} / {info.Location.Container}");
        Add(fields, "Revision", revisions);
        Add(fields, "Last change", $"{info.RemoteUpdatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)} by {Domain.Paths.PathErrors.Display(info.RemoteUpdatedBy)}");
        Add(fields, "Created", info.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Add(fields, "Files", $"{info.FileCount} ({ConsoleOutput.Bytes(info.TotalBytes)})");
        Add(fields, "Compression", info.Compression == Domain.Manifests.CompressionSetting.Gzip ? "on (gzip)" : "off");
        if (info.OpenConflicts is { } conflicts)
        {
            Add(fields, "Conflicts", conflicts == 0 ? "none" : $"{conflicts} open (mysync conflicts)");
        }

        ConsoleOutput.Write(fields);
    }

    /// <summary><c>mysync compression [on|off]</c>.</summary>
    public static void Render(Application.Shares.Compression.CompressionResponse compression, bool changing)
    {
        ArgumentNullException.ThrowIfNull(compression);

        string share = ConsoleOutput.Value(compression.Name?.Value ?? compression.ShareId.ToString());
        bool on = compression.Setting == Domain.Manifests.CompressionSetting.Gzip;
        if (!changing)
        {
            AnsiConsole.MarkupLine($"Compression is {(on ? "[green]on[/] (gzip)" : "off")} for {share}.");
            return;
        }

        if (!compression.Changed)
        {
            AnsiConsole.MarkupLine($"Compression is already {(on ? "on" : "off")} for {share}; nothing changed.");
            return;
        }

        AnsiConsole.MarkupLine(on
            ? $"[green]Compression is on[/] for {share}. Files uploaded from now on are stored compressed."
            : $"[green]Compression is off[/] for {share}. Files uploaded from now on are stored as they are; ones already stored compressed stay so.");
        if (on)
        {
            AnsiConsole.MarkupLine(ConsoleOutput.Muted("Every machine that syncs this share needs mysync 0.7.5 or later; older ones say \"upgrade mysync\"."));
        }
    }

    /// <summary><c>mysync shares</c>: name and full share ID (copyable), sorted by name.</summary>
    public static void Render(ShareListResponse listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        if (listed.Shares.Count == 0)
        {
            AnsiConsole.MarkupLine("No named shares yet. Name a share with: mysync rename <name>");
            return;
        }

        Table table = new Table().Border(TableBorder.None).HideRowSeparators();
        table.AddColumn(new TableColumn("[bold]NAME[/]").PadLeft(0).PadRight(2));
        table.AddColumn(new TableColumn("[bold]SHARE ID[/]"));
        foreach (Domain.Shares.AliasRecord share in listed.Shares)
        {
            table.AddRow(ConsoleOutput.Value(share.Name.Value), ConsoleOutput.Value(share.ShareId.ToString()));
        }

        AnsiConsole.Write(table);
    }

    /// <summary><c>mysync rename</c>.</summary>
    public static void Render(RenameShareResponse renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);

        if (renamed.Unchanged)
        {
            AnsiConsole.MarkupLine($"The share is already named {ConsoleOutput.Value(renamed.Name.Value)}; nothing changed.");
            return;
        }

        string previous = renamed.Previous.Count == 0 ? string.Empty : $" (was {string.Join(", ", renamed.Previous.Select(name => name.Value))})";
        AnsiConsole.MarkupLine($"[green]Renamed[/] share {ConsoleOutput.Value(renamed.ShareId.ToString())} to {ConsoleOutput.Value(renamed.Name.Value)}{ConsoleOutput.Value(previous)}.");
        AnsiConsole.MarkupLine($"Clone it with:  [bold]{ConsoleOutput.Value($"mysync sync {renamed.Name}")}[/]");
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
        string names = deleted.NamesFreed.Count == 0 ? string.Empty : $" Freed the name {string.Join(", ", deleted.NamesFreed.Select(name => name.Value))}.";
        if (deleted.WasIncomplete)
        {
            AnsiConsole.MarkupLine($"[green]Removed the unfinished share {ConsoleOutput.Value(deleted.ShareId.ToString())}[/]: {deleted.ObjectsDeleted} stored {files} ({ConsoleOutput.Bytes(deleted.BytesDeleted)}).{ConsoleOutput.Value(names)}");
            return;
        }

        AnsiConsole.MarkupLine(deleted.WasAlreadyDeleted
            ? $"[green]Share {ConsoleOutput.Value(deleted.ShareId.ToString())} was already deleted[/] by {ConsoleOutput.Value(Domain.Paths.PathErrors.Display(deleted.Deletion?.DeletedBy ?? "?"))}; finished removing what was left.{ConsoleOutput.Value(names)}"
            : $"[green]Deleted share {ConsoleOutput.Value(deleted.ShareId.ToString())}[/]: {deleted.ObjectsDeleted} stored {files} ({ConsoleOutput.Bytes(deleted.BytesDeleted)}) and its history are gone.{ConsoleOutput.Value(names)}");
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
