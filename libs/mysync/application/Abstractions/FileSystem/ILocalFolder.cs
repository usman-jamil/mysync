using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Application.Abstractions.FileSystem;

/// <summary>
/// The synced folder on this machine: every read and write the sync does to it (CLAUDE.md §9
/// phase 8). Implemented in Infrastructure, which also maps each <see cref="SharePath"/> to a
/// full path and checks it stays inside the root. Faked by <c>InMemoryLocalFolder</c>.
/// </summary>
public interface ILocalFolder
{
    /// <summary>Full path of the share root, for messages.</summary>
    string RootPath { get; }

    /// <summary>The home directory, a drive or volume root, or <c>/</c>: refuse to create a share here without <c>--force</c>.</summary>
    bool IsUnsafeRoot { get; }

    /// <summary>
    /// False on case-insensitive filesystems (Windows, default macOS): there, entries that differ
    /// only in case are not downloaded (CLAUDE.md §8).
    /// </summary>
    bool IsCaseSensitive { get; }

    /// <summary>True on Windows: entries whose names Windows can't hold are skipped with a warning.</summary>
    bool RequiresWindowsCompatibleNames { get; }

    /// <summary>Names directly in the root (for the clone "folder must be empty" check).</summary>
    Task<Result<IReadOnlyList<string>>> ListTopLevelNamesAsync(CancellationToken cancellationToken);

    /// <summary>Size and mtime of a file, or <c>null</c> if nothing (or not a regular file) is there. Used to re-check before overwriting.</summary>
    Task<Result<LocalFile?>> GetFileAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>
    /// What is at <paramref name="path"/>: nothing, a file, a directory, or something else (a symlink,
    /// a device). Fails when the path can't be used here (a symlink on the way).
    /// </summary>
    Task<Result<LocalEntryKind>> GetEntryKindAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>A seekable read stream that reports its length (the upload needs both).</summary>
    Task<Result<Stream>> OpenReadAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>A new file in the staging directory (<c>.mysynctool.tmp/</c>, or <c>.mysync-pull.tmp/</c> for a pull; same volume) to download into.</summary>
    Task<Result<IStagedFile>> CreateStagedFileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves a staged file into place atomically, replacing a file or empty directory at
    /// <paramref name="target"/> and creating parent directories, then applies mtime and the
    /// executable bit (the bit is ignored on Windows).
    /// </summary>
    Task<Result> PlaceStagedFileAsync(IStagedFile staged, SharePath target, DateTimeOffset mtimeUtc, bool executable, CancellationToken cancellationToken);

    /// <summary>Sets or clears the executable bit of an existing file. A no-op on Windows.</summary>
    Task<Result> SetExecutableAsync(SharePath path, bool executable, CancellationToken cancellationToken);

    /// <summary>Creates a directory (and its parents). An existing directory counts as success.</summary>
    Task<Result> CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>Deletes a file. A missing file counts as success.</summary>
    Task<Result> DeleteFileAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>Deletes a directory if it is empty. Returns whether it was deleted; missing or non-empty → false.</summary>
    Task<Result<bool>> DeleteDirectoryIfEmptyAsync(SharePath path, CancellationToken cancellationToken);

    /// <summary>Empties the staging directory (start and end of every run).</summary>
    Task<Result> CleanStagingAsync(CancellationToken cancellationToken);
}

/// <summary>A download in progress. Disposing it without placing it deletes it.</summary>
public interface IStagedFile : IAsyncDisposable
{
    /// <summary>Write the downloaded bytes here.</summary>
    Stream Content { get; }
}

/// <summary>What <see cref="ILocalFolder.GetEntryKindAsync"/> found.</summary>
public enum LocalEntryKind
{
    None,
    File,
    Directory,

    /// <summary>A symlink, junction or anything that isn't a regular file or directory.</summary>
    Other,
}

/// <summary>
/// Opens folders other than the share root for writing, with the same rules as the share root
/// (every path through the resolver, nothing outside the root, symlinks on the way refused):
/// <c>mysync pull</c>'s destination. Never touches sync state.
/// </summary>
public interface ILocalFolderFactory
{
    /// <summary>
    /// The folder at <paramref name="path"/> (absolute, or relative to the current folder; <c>null</c>
    /// for the current folder itself), staging downloads in its own <c>.mysync-pull.tmp/</c>. It
    /// needn't exist: it is created by the first write.
    /// </summary>
    ILocalFolder OpenPullDestination(string? path);
}

/// <summary>What a file looked like at one moment: compared again before it is overwritten or deleted.</summary>
public sealed record LocalFile(SharePath Path, long Size, DateTimeOffset MtimeUtc);
