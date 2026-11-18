namespace MySync.Infrastructure.Updates;

/// <summary>
/// What an update can leave next to the executable: on Windows the previous binary, renamed aside
/// because a running .exe can't be deleted (<c>mysync.exe.old-…</c>), and a workspace an interrupted
/// update didn't remove. The CLI calls this once at start-up; it is quick and never fails the run.
/// </summary>
public static class UpdateLeftovers
{
    /// <summary>A workspace younger than this may belong to an update running right now.</summary>
    private static readonly TimeSpan WorkspaceGrace = TimeSpan.FromHours(1);

    public static void RemoveNextTo(string? executablePath)
    {
        if (executablePath is null || string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? folder = Path.GetDirectoryName(executablePath);
        if (folder is null)
        {
            return;
        }

        try
        {
            foreach (string old in Directory.EnumerateFiles(folder, Path.GetFileName(executablePath) + ".old-*"))
            {
                TryDelete(() => File.Delete(old));
            }

            foreach (string workspace in Directory.EnumerateDirectories(folder, ExecutableInstaller.WorkspacePrefix + "*"))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(workspace) > WorkspaceGrace)
                {
                    TryDelete(() => Directory.Delete(workspace, recursive: true));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not ours to worry about now; the next start tries again.
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still in use (an old copy still running); a later start removes it.
        }
    }
}
