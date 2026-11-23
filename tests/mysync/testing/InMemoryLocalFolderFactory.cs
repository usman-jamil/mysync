using MySync.Application.Abstractions.FileSystem;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// Pull destinations for handler tests: one <see cref="InMemoryLocalFolder"/> per path (relative
/// paths are taken from <see cref="CurrentDirectory"/>), made on first use by <see cref="Create"/>.
/// </summary>
public sealed class InMemoryLocalFolderFactory(IDateTimeProvider? clock = null) : ILocalFolderFactory
{
    private readonly Dictionary<string, InMemoryLocalFolder> _folders = new(StringComparer.Ordinal);

    /// <summary>The folder a command runs in.</summary>
    public string CurrentDirectory { get; set; } = "/home/usman/collect";

    /// <summary>Makes a destination folder for a full path; replace to simulate Windows or a case-insensitive disk.</summary>
    public Func<string, InMemoryLocalFolder>? Create { get; set; }

    /// <summary>Every destination opened, in order.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>The folder at <paramref name="fullPath"/> (made if it wasn't yet), for setting up and checking a test.</summary>
    public InMemoryLocalFolder Folder(string fullPath)
    {
        lock (_folders)
        {
            if (!_folders.TryGetValue(fullPath, out InMemoryLocalFolder? folder))
            {
                folder = Create?.Invoke(fullPath) ?? new InMemoryLocalFolder(clock) { RootPath = fullPath };
                _folders.Add(fullPath, folder);
            }

            return folder;
        }
    }

    public ILocalFolder OpenPullDestination(string? path)
    {
        string full = CurrentDirectory;
        if (!string.IsNullOrEmpty(path))
        {
            full = path.StartsWith('/') ? path.TrimEnd('/') : $"{CurrentDirectory}/{path.TrimEnd('/')}";
        }

        lock (_folders)
        {
            Opened.Add(full);
        }

        return Folder(full);
    }
}
