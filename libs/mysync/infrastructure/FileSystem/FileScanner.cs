using System.Collections.Concurrent;
using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// Walks the share root and hashes it (CLAUDE.md §8, §9 phase 2).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Ignored paths are skipped and ignored directories not entered. mysync's own artifacts in
/// the root are skipped whatever the matcher says.</item>
/// <item>Symlinks and junctions are reported as skipped, never followed.</item>
/// <item>Names are normalized to NFC; a name no <see cref="SharePath"/> can hold (a backslash on
/// Linux, or two names in one directory that are the same once normalized) is skipped.</item>
/// <item>A cached hash is reused only when size and mtime match exactly. A file whose mtime is
/// within 2 seconds of the moment it was hashed is not cached (the racy-mtime rule).</item>
/// <item>Each file is re-stated after hashing; if it changed meanwhile it is skipped as unstable.</item>
/// <item>A directory whose every entry is ignored counts as empty, so the share keeps it.</item>
/// <item>An empty file is not opened: its hash is known, and FIFOs, sockets and devices (which
/// report size 0 and could block a read forever) are never read.</item>
/// </list>
/// Hashing runs in parallel; results are sorted ordinally, so a scan is deterministic.
/// </remarks>
internal sealed class FileScanner(ShareRoot root, IContentHasher hasher, IDateTimeProvider clock) : IFileScanner
{
    internal static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    private const int BufferSize = 128 * 1024;

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public async Task<Result<ScannedFolder>> ScanAsync(ScanRequest request, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<Walk> walked = WalkTree(request.Ignore, cancellationToken);
        if (walked.IsFailure)
        {
            return Result.Failure<ScannedFolder>(walked.Error);
        }

        Walk walk = walked.Value;
        ConcurrentBag<(SharePath Path, FileEntry Entry, LocalFile File, HashCacheEntry? Cache)> hashed = [];
        ConcurrentBag<SkippedLocalPath> skipped = [.. walk.Skipped];
        ContentHash empty = await hasher.HashAsync(Stream.Null, cancellationToken);

        await Parallel.ForEachAsync(
            walk.Files,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                bool executable = OperatingSystem.IsWindows()
                    ? request.Base.GetValueOrDefault(file.Path) is FileEntry { Executable: true }
                    : file.Executable;

                HashOutcome outcome = await HashAsync(file, request.HashCache, empty, ct);
                if (outcome.Hash is null)
                {
                    skipped.Add(new SkippedLocalPath(file.Path.Value, outcome.Problem, outcome.Detail));
                    return;
                }

                FileEntry entry = FileEntry.Create(outcome.Hash, file.Size, file.MtimeUtc, executable).Value;
                hashed.Add((file.Path, entry, new LocalFile(file.Path, file.Size, file.MtimeUtc), outcome.Cache));
            });

        ImmutableSortedDictionary<SharePath, ManifestEntry>.Builder entries = ImmutableSortedDictionary.CreateBuilder<SharePath, ManifestEntry>(SharePath.OrdinalComparer);
        ImmutableSortedDictionary<SharePath, LocalFile>.Builder files = ImmutableSortedDictionary.CreateBuilder<SharePath, LocalFile>(SharePath.OrdinalComparer);
        ImmutableSortedDictionary<SharePath, HashCacheEntry>.Builder cache = ImmutableSortedDictionary.CreateBuilder<SharePath, HashCacheEntry>(SharePath.OrdinalComparer);

        foreach (SharePath directory in walk.EmptyDirectories)
        {
            entries[directory] = DirectoryEntry.Instance;
        }

        foreach ((SharePath path, FileEntry entry, LocalFile file, HashCacheEntry? cached) in hashed)
        {
            entries[path] = entry;
            files[path] = file;
            if (cached is not null)
            {
                cache[path] = cached;
            }
        }

        return new ScannedFolder(
            entries.ToImmutable(),
            files.ToImmutable(),
            cache.ToImmutable(),
            [.. skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)]);
    }

    public Task<Result<FolderSummary>> SummarizeAsync(IIgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ignore);

        Result<Walk> walked = WalkTree(ignore, cancellationToken);

        return Task.FromResult(walked.IsFailure
            ? Result.Failure<FolderSummary>(walked.Error)
            : Result.Success(new FolderSummary(walked.Value.Files.Count, walked.Value.Files.Sum(file => file.Size))));
    }

    private async Task<HashOutcome> HashAsync(
        FoundFile file, ImmutableSortedDictionary<SharePath, HashCacheEntry> previous, ContentHash empty, CancellationToken cancellationToken)
    {
        if (previous.TryGetValue(file.Path, out HashCacheEntry? cached) && cached.Matches(file.Size, file.MtimeUtc.UtcTicks))
        {
            return new HashOutcome(cached.Sha256, cached);
        }

        ContentHash hash;
        long read;
        if (file.Size == 0)
        {
            (hash, read) = (empty, 0);
        }
        else
        {
            try
            {
                FileStream stream = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (stream)
                {
                    hash = await hasher.HashAsync(stream, cancellationToken);
                    read = stream.Position;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return HashOutcome.Skip(LocalSkipReason.Unreadable, exception.Message);
            }
        }

        DateTime hashedAt = clock.UtcNow;
        FileInfo after = new(file.FullPath);
        if (!after.Exists || after.Length != file.Size || after.LastWriteTimeUtc != file.MtimeUtc.UtcDateTime || read != file.Size)
        {
            return HashOutcome.Skip(LocalSkipReason.Unstable, "it changed while it was being hashed");
        }

        // Racy mtime: a same-second edit after this moment could keep size and mtime unchanged.
        bool racy = (hashedAt - file.MtimeUtc.UtcDateTime).Duration() < RacyWindow;
        return new HashOutcome(hash, racy ? null : new HashCacheEntry(file.Size, file.MtimeUtc.UtcTicks, hash));
    }

    private Result<Walk> WalkTree(IIgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        Walk walk = new();
        Result<int> top = WalkDirectory(root.FullPath, null, ignore, walk, cancellationToken);

        return top.IsFailure ? Result.Failure<Walk>(top.Error) : walk;
    }

    // Returns how many entries in the directory take part in the sync (so 0 means "empty").
    private Result<int> WalkDirectory(string fullPath, SharePath? directory, IIgnoreMatcher ignore, Walk walk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<FileSystemInfo> children;
        try
        {
            children = [.. new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", OneLevel)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<int>(LocalFileErrors.Unreadable(directory?.Value ?? ".", exception.Message));
        }

        children.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        HashSet<string> seen = new(StringComparer.Ordinal);
        int taking = 0;

        foreach (FileSystemInfo child in children)
        {
            string name = PathRules.ToNfc(child.Name);
            string relative = directory is null ? name : $"{directory.Value}{SharePath.Separator}{name}";
            bool isDirectory = child is DirectoryInfo;

            if (directory is null && StateFileNames.AlwaysIgnored.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Result<SharePath> path = SharePath.Create(relative);
            if (path.IsFailure)
            {
                taking++;
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.InvalidName, path.Error.Description));
                continue;
            }

            if (ignore.IsIgnored(path.Value, isDirectory))
            {
                continue;
            }

            taking++;

            if (!seen.Add(name))
            {
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.InvalidName, "another name in this folder is the same once Unicode-normalized"));
                continue;
            }

            if (child.LinkTarget is not null)
            {
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.Symlink, "symlinks and junctions are not synced"));
                continue;
            }

            if (isDirectory)
            {
                Result<int> inside = WalkDirectory(child.FullName, path.Value, ignore, walk, cancellationToken);
                if (inside.IsFailure)
                {
                    walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.Unreadable, inside.Error.Description));
                }
                else if (inside.Value == 0)
                {
                    walk.EmptyDirectories.Add(path.Value);
                }

                continue;
            }

            var file = (FileInfo)child;
            bool executable = !OperatingSystem.IsWindows() && (file.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            walk.Files.Add(new FoundFile(path.Value, file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc), executable));
        }

        return taking;
    }

    private sealed record HashOutcome(ContentHash? Hash, HashCacheEntry? Cache, LocalSkipReason Problem = default, string Detail = "")
    {
        public static HashOutcome Skip(LocalSkipReason problem, string detail) => new(null, null, problem, detail);
    }

    private sealed record FoundFile(SharePath Path, string FullPath, long Size, DateTimeOffset MtimeUtc, bool Executable);

    private sealed class Walk
    {
        public List<FoundFile> Files { get; } = [];

        public List<SharePath> EmptyDirectories { get; } = [];

        public List<SkippedLocalPath> Skipped { get; } = [];
    }
}
