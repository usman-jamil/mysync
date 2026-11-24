using SharedKernel;

namespace MySync.Domain.State;

public static class StateErrors
{
    public static Error Corrupt(string detail) => Error.Problem(
        "State.Corrupt",
        $"The state file {StateFileNames.State} is unreadable or damaged ({detail}). mysync stopped without changing anything.");

    public static Error UnsupportedFormat(int formatVersion) => Error.Problem(
        "State.UnsupportedFormat",
        $"{StateFileNames.State} uses format {formatVersion}, which is newer than this mysync supports. Upgrade mysync.");

    public static readonly Error FolderLocked = Error.Conflict(
        "State.FolderLocked",
        "Another mysync process is working in this folder. Wait for it to finish and try again.");

    // Account and container names are not secrets; the connection string never appears here.
    public static Error StorageMismatch(StorageLocation recorded, StorageLocation current)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(current);
        string fix = string.Equals(recorded.Provider, current.Provider, StringComparison.Ordinal)
            ? $"Point the environment at {recorded.Describe()} ({StorageLocation.SettingsOf(recorded.Provider)})."
            : $"Set MYSYNC_PROVIDER={recorded.Provider} and its settings ({StorageLocation.SettingsOf(recorded.Provider)}) to sync this folder.";
        return Error.Conflict(
            "State.StorageMismatch",
            $"This folder belongs to {recorded.Describe()}, but the environment points at {current.Describe()}. Nothing was changed. {fix}");
    }
}
