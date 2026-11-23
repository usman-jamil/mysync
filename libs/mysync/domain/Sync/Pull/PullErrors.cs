using System.Globalization;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Pull;

/// <summary>Why <c>mysync pull</c> stopped before downloading anything (CLAUDE.md §10c).</summary>
public static class PullErrors
{
    /// <summary>A share path that is neither a file nor a folder in HEAD. Exit 2.</summary>
    public static Error PathNotFound(string path) => Error.Validation(
        "Pull.PathNotFound",
        $"'{PathErrors.Display(path)}' isn't a file or folder in the share. Nothing was pulled.");

    /// <summary>Two share paths would land at the same name in the destination. Exit 2.</summary>
    public static Error DuplicateTarget(string name, string first, string second) => Error.Validation(
        "Pull.DuplicateTarget",
        $"'{PathErrors.Display(first)}' and '{PathErrors.Display(second)}' would both be pulled as '{PathErrors.Display(name)}'. Pull them separately, with --to.");

    /// <summary><c>--host</c> named a host with no push folder. Exit 2.</summary>
    public static Error HostNotFound(string host) => Error.Validation(
        "Pull.HostNotFound",
        $"The share has no pushed folder for host '{PathErrors.Display(host)}' (<host>/<timestamp>/).");

    /// <summary><c>--latest</c> picks the folders itself. Exit 2.</summary>
    public static Error LatestWithPaths() => Error.Validation(
        "Pull.LatestWithPaths",
        "--latest picks the newest pushed folder of each host itself; don't name share paths with it.");

    /// <summary><c>--host</c> only narrows <c>--latest</c>. Exit 2.</summary>
    public static Error HostNeedsLatest() => Error.Validation(
        "Pull.HostNeedsLatest",
        "--host only works with --latest: mysync pull <share> --latest --host <name>");

    /// <summary><c>--latest</c> in a share nothing was pushed into. Exit 8.</summary>
    public static Error NoPushes(string share) => Error.NotFound(
        "Pull.NoPushes",
        $"{share} has no pushed folders (<host>/<timestamp>/).");

    /// <summary>Files at the destination differ from what the pull would write, or are in its way. Exit 4.</summary>
    public static Error DestinationOccupied(int count, IReadOnlyList<string> firstPaths, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(firstPaths);

        string shown = string.Join(", ", firstPaths.Select(path => $"'{PathErrors.Display(path)}'"));
        string more = count > firstPaths.Count ? ", …" : string.Empty;
        string what = count == 1 ? "1 path at the destination is" : string.Create(CultureInfo.InvariantCulture, $"{count} paths at the destination are");
        string advice = overwrite
            ? "--overwrite only replaces files with files; move these out of the way."
            : "Move them, or pass --overwrite to replace files whose content differs.";
        return Error.Conflict("Pull.DestinationOccupied", $"{what} in the way: {shown}{more}. Nothing was pulled. {advice}");
    }

    /// <summary>The destination (or a parent) already syncs with this share. Exit 4; nothing overrides it.</summary>
    public static Error DestinationIsTheShare(string folder, string share) => Error.Conflict(
        "Pull.DestinationIsTheShare",
        $"This folder ('{PathErrors.Display(folder)}') already syncs with {share}; use mysync sync. Files pulled here would be uploaded again as new files by the next sync.");
}
