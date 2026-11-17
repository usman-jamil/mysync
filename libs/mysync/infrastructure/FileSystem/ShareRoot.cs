namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// The folder being synced: the current directory for the CLI, a temp folder in tests. Every
/// local-machine service (folder, scanner, state file, lock, ignore rules, conflict log) works
/// inside it.
/// </summary>
public sealed class ShareRoot
{
    public ShareRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Full and without a trailing separator ("/" and "C:\" keep theirs). Symlinks in the root
        // itself are not resolved: the user chose this folder.
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public string FullPath { get; }

    public string Combine(string name) => Path.Combine(FullPath, name);

    public override string ToString() => FullPath;
}
