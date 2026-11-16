using MySync.Domain.Content;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Domain.Manifests;

public enum EntryType
{
    File = 0,
    Directory = 1,
}

/// <summary>
/// One path in a manifest (or in a local scan, or in the base): a file or an empty directory.
/// "Absent" is represented by <c>null</c>, never by an entry.
/// </summary>
public abstract record ManifestEntry
{
    private protected ManifestEntry()
    {
    }

    public abstract EntryType Type { get; }

    /// <summary>
    /// The comparison the merge uses (CLAUDE.md §9): type, SHA-256 and, for files, the executable
    /// bit, so a <c>chmod +x</c> alone is a change that syncs. Size follows from the hash. mtime is
    /// not compared: touching a file without changing it is not a change. Two absent entries are
    /// equivalent.
    /// </summary>
    /// <remarks>
    /// When only <see cref="FileEntry.Executable"/> differs, the bytes are identical, so applying
    /// the change needs no upload or download, only the manifest entry or the local file mode.
    /// </remarks>
    public static bool AreEquivalent(ManifestEntry? left, ManifestEntry? right) => (left, right) switch
    {
        (null, null) => true,
        (FileEntry l, FileEntry r) => l.Sha256 == r.Sha256 && l.Executable == r.Executable,
        (DirectoryEntry, DirectoryEntry) => true,
        _ => false,
    };
}

public sealed record FileEntry : ManifestEntry
{
    private FileEntry(ContentHash sha256, long size, DateTimeOffset mtimeUtc, bool executable)
    {
        Sha256 = sha256;
        Size = size;
        MtimeUtc = mtimeUtc;
        Executable = executable;
    }

    public override EntryType Type => EntryType.File;

    public ContentHash Sha256 { get; }

    public long Size { get; }

    public DateTimeOffset MtimeUtc { get; }

    public bool Executable { get; }

    public static Result<FileEntry> Create(ContentHash sha256, long size, DateTimeOffset mtime, bool executable)
    {
        ArgumentNullException.ThrowIfNull(sha256);

        if (size < 0)
        {
            return Result.Failure<FileEntry>(ShareErrors.InvalidManifest($"a file size is negative ({size})."));
        }

        return new FileEntry(sha256, size, mtime.ToUniversalTime(), executable);
    }
}

public sealed record DirectoryEntry : ManifestEntry
{
    private DirectoryEntry()
    {
    }

    /// <summary>A directory entry carries no data, so one instance serves every empty directory.</summary>
    public static DirectoryEntry Instance { get; } = new();

    public override EntryType Type => EntryType.Directory;
}
