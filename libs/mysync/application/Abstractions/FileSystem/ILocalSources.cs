using System.Collections.Immutable;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Abstractions.FileSystem;

/// <summary>
/// Files and folders anywhere on this machine, read by <c>mysync push</c> (CLAUDE.md §10b). Unlike
/// <see cref="ILocalFolder"/> and <see cref="IFileScanner"/> it isn't bound to one share root and
/// knows nothing of sync state: no hash cache, no ignore rules, no lock, no staging directory, and
/// it never writes. <see cref="ListAsync"/> is the one place push's file enumeration happens, so
/// filters (later work) belong there.
/// </summary>
public interface ILocalSources
{
    /// <summary>The folder <c>mysync push</c> runs in: pushed when no paths are given.</summary>
    LocalSourceInfo CurrentFolder();

    /// <summary>What is at <paramref name="path"/> (absolute, or relative to <see cref="CurrentFolder"/>). Never fails: a missing path is <see cref="LocalSourceKind.Missing"/>.</summary>
    LocalSourceInfo Inspect(string path);

    /// <summary>
    /// Every <c>.mysynctool</c> in the folder <paramref name="path"/> names (for a file, the folder
    /// it is in) and each of its parents, with the share it records. Read only, nothing else of the
    /// state is used. An unreadable or damaged one is <c>State.Corrupt</c>: push doesn't guess. The
    /// path is the source's <see cref="LocalSourceInfo.FullPath"/>; only the implementation knows
    /// how its paths split into folders.
    /// </summary>
    Task<Result<IReadOnlyList<SyncedFolder>>> FindSyncedFoldersAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Walks the sources: files (size, mtime, executable bit), empty folders, and what was skipped
    /// (symlinks and junctions, unreadable folders, names no <see cref="SharePath"/> can hold). Paths
    /// are relative to the target folder, NFC, sorted ordinally. Only mysync's own artifacts
    /// (<c>.mysynctool*</c>) and OS clutter are left out, at any depth; <c>.mysyncignore</c> is an
    /// ordinary file here.
    /// </summary>
    Task<Result<SourceListing>> ListAsync(IReadOnlyList<SourceToList> sources, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a file for reading while others may keep writing it. The stream is seekable, and its
    /// <see cref="Stream.Length"/> at this moment is what gets captured. Locked, unreadable or gone
    /// is <c>Local.Unreadable</c>.
    /// </summary>
    Task<Result<OpenedSourceFile>> OpenAsync(string fullPath, CancellationToken cancellationToken);
}

public enum LocalSourceKind
{
    Missing = 0,
    File = 1,
    Folder = 2,

    /// <summary>A symlink or junction: not followed, not pushed.</summary>
    Symlink = 3,
}

/// <param name="Argument">As typed (or the full path, for the current folder).</param>
/// <param name="Name">The last segment of <paramref name="FullPath"/>; empty for a filesystem root.</param>
/// <param name="IsUnsafeRoot">A folder that is the home directory, a drive or volume root, or <c>/</c>.</param>
public sealed record LocalSourceInfo(string Argument, string FullPath, LocalSourceKind Kind, string Name, bool IsUnsafeRoot);

/// <summary>A folder with a <c>.mysynctool</c> that records <paramref name="ShareId"/>.</summary>
public sealed record SyncedFolder(string FullPath, ShareId ShareId);

/// <param name="Root">Where it lands under the target folder; <c>null</c> for a folder whose contents land directly there.</param>
public sealed record SourceToList(string FullPath, bool IsFolder, SharePath? Root);

/// <param name="Path">Relative to the target folder.</param>
public sealed record SourceFile(SharePath Path, string FullPath, long Size, DateTimeOffset MtimeUtc, bool Executable);

/// <param name="EmptyFolders">Relative to the target folder; a folder whose every entry is left out counts as empty.</param>
/// <param name="Skipped">Paths relative to the target folder (as found, maybe not valid share paths).</param>
public sealed record SourceListing(ImmutableArray<SourceFile> Files, ImmutableArray<SharePath> EmptyFolders, ImmutableArray<SkippedLocalPath> Skipped);

/// <summary>An open source file. Disposing it closes <see cref="Content"/>.</summary>
/// <param name="Content">Seekable; its length when opened is the captured length.</param>
/// <param name="MtimeUtc">Its last-write time when opened.</param>
public sealed record OpenedSourceFile(Stream Content, DateTimeOffset MtimeUtc) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
