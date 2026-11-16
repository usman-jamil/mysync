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
}
