using System.Collections.Immutable;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Abstractions.FileSystem;

/// <summary>
/// Walks the local folder and hashes it (CLAUDE.md §9 phase 2): skips ignored paths and
/// symlinks, reuses <see cref="ScanRequest.HashCache"/> where size and mtime match, re-stats each
/// file after hashing and leaves out files that changed meanwhile, normalizes paths to NFC.
/// </summary>
public interface IFileScanner
{
    Task<Result<ScannedFolder>> ScanAsync(ScanRequest request, ISyncProgressReporter progress, CancellationToken cancellationToken);

    /// <summary>File count and total size, without hashing (the create preview, CLAUDE.md §9).</summary>
    Task<Result<FolderSummary>> SummarizeAsync(IIgnoreMatcher ignore, CancellationToken cancellationToken);
}

public sealed record FolderSummary(int FileCount, long TotalBytes);

/// <param name="Ignore">Built-in excludes plus <c>.mysyncignore</c>.</param>
/// <param name="HashCache">From <c>.mysynctool</c>; empty for a new share.</param>
/// <param name="Base">Where the platform can't see the executable bit (Windows), the scanner takes it from here.</param>
public sealed record ScanRequest(
    IIgnoreMatcher Ignore,
    ImmutableSortedDictionary<SharePath, HashCacheEntry> HashCache,
    IReadOnlyDictionary<SharePath, ManifestEntry> Base);

/// <param name="Entries">The local side of the merge (files and empty directories).</param>
/// <param name="Files">Size and mtime of every scanned file, for the re-check before overwriting.</param>
/// <param name="HashCache">The cache to store after this run (racy-mtime entries left out).</param>
/// <param name="Skipped">Paths left out of this run, with why.</param>
public sealed record ScannedFolder(
    ImmutableSortedDictionary<SharePath, ManifestEntry> Entries,
    ImmutableSortedDictionary<SharePath, LocalFile> Files,
    ImmutableSortedDictionary<SharePath, HashCacheEntry> HashCache,
    ImmutableArray<SkippedLocalPath> Skipped);

public enum LocalSkipReason
{
    /// <summary>Changed while it was being hashed.</summary>
    Unstable = 0,

    /// <summary>Locked or unreadable (common on Windows).</summary>
    Unreadable = 1,

    /// <summary>A name no <see cref="SharePath"/> can hold (e.g. contains a backslash or a control character).</summary>
    InvalidName = 2,

    /// <summary>Symlink or junction: not followed, not synced (v1).</summary>
    Symlink = 3,
}

/// <param name="Path">As found on disk, which may not be a valid <see cref="SharePath"/>. Display it with <see cref="PathErrors.Display"/>.</param>
public sealed record SkippedLocalPath(string Path, LocalSkipReason Reason, string Detail);
