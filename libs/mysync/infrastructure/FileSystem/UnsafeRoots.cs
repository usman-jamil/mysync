namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// Folders a share must not be created in without <c>--force</c> (CLAUDE.md §9): the home
/// directory, a drive or volume root, or <c>/</c>. Syncing one of those by accident would upload
/// far more than intended.
/// </summary>
internal static class UnsafeRoots
{
    public static bool IsUnsafe(string fullPath)
    {
        string path = Path.TrimEndingDirectorySeparator(fullPath);

        if (string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? path), PathComparison))
        {
            return true; // "/", "C:\"
        }

        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && string.Equals(path, Path.TrimEndingDirectorySeparator(home), PathComparison))
        {
            return true;
        }

        return MountPoints().Any(mount => string.Equals(path, mount, PathComparison));
    }

    // Windows and default macOS volumes ignore case, so "c:\" is "C:\".
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Volume roots: other drives, /Volumes/USB on macOS, /mnt/data on Linux.
    private static IEnumerable<string> MountPoints()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            yield return Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName);
        }
    }
}
