using System.Text;

namespace MySync.Testing;

/// <summary>
/// A real, empty temp directory for disk tests, deleted on dispose (permissions restored first, so
/// a test that made something unreadable still cleans up). Paths are written with <c>/</c>.
/// </summary>
public sealed class TempShareFolder : IDisposable
{
    public TempShareFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mysync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public TempShareFolder WriteFile(string relative, string content, DateTime? mtimeUtc = null, bool executable = false) =>
        WriteFile(relative, Encoding.UTF8.GetBytes(content), mtimeUtc, executable);

    public TempShareFolder WriteFile(string relative, byte[] content, DateTime? mtimeUtc = null, bool executable = false)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        if (mtimeUtc is { } mtime)
        {
            File.SetLastWriteTimeUtc(full, mtime);
        }

        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, File.GetUnixFileMode(full) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return this;
    }

    public TempShareFolder CreateDirectory(string relative)
    {
        Directory.CreateDirectory(Combine(relative));
        return this;
    }

    public bool Exists(string relative) => System.IO.Path.Exists(Combine(relative));

    public string ReadText(string relative) => File.ReadAllText(Combine(relative));

    public bool IsExecutable(string relative) =>
        !OperatingSystem.IsWindows() && (File.GetUnixFileMode(Combine(relative)) & UnixFileMode.UserExecute) != 0;

    /// <summary>Every file under the folder (mysync's own artifacts excluded) with its text, by <c>/</c> path, in ordinal order.</summary>
    public IReadOnlyDictionary<string, string> Files() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Select(full => System.IO.Path.GetRelativePath(Path, full).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.StartsWith(".mysynctool", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(relative => relative, ReadText, StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (string directory in Directory.EnumerateDirectories(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Prepend(Path))
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            foreach (string file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: it's in the temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
