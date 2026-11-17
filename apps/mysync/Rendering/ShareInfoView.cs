using System.Globalization;
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

    private static void Add(Grid fields, string label, string value) => fields.AddRow(ConsoleOutput.Label(label), ConsoleOutput.Value(value));
}
