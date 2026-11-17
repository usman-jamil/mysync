namespace MySync.Domain.State;

/// <summary>
/// Names of the local artifacts mysync keeps inside a synced folder. They are always ignored
/// and never synced. Renaming the tool means changing them here (see CLAUDE.md).
/// </summary>
public static class StateFileNames
{
    public const string State = ".mysynctool";

    public const string StateTemp = ".mysynctool.new";

    public const string Lock = ".mysynctool.lock";

    public const string StagingDirectory = ".mysynctool.tmp";

    public const string ConflictLog = ".mysynctool.log";

    public const string RotatedConflictLog = ".mysynctool.log.1";

    public const string IgnoreFile = ".mysyncignore";

    /// <summary>
    /// Every artifact above except <see cref="IgnoreFile"/>, which is synced on purpose.
    /// </summary>
    public static IReadOnlyList<string> AlwaysIgnored { get; } =
        [State, StateTemp, Lock, StagingDirectory, ConflictLog, RotatedConflictLog];

    /// <summary>
    /// Files operating systems drop into folders on their own (Finder, Explorer). Always ignored,
    /// never make a folder "not empty" for a clone, and never keep a directory from being deleted.
    /// </summary>
    public static IReadOnlyList<string> OsClutter { get; } = [".DS_Store", "Thumbs.db", "desktop.ini"];

    /// <summary>macOS resource-fork files (<c>._name</c>), written next to files on non-Apple filesystems.</summary>
    public const string ResourceForkPrefix = "._";

    /// <summary>Whether a file name is <see cref="OsClutter"/> or a resource fork. Ignores case, as those systems do.</summary>
    public static bool IsOsClutter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.StartsWith(ResourceForkPrefix, StringComparison.Ordinal)
            || OsClutter.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
