using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.State;
using SharedKernel;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// What <c>mysync push</c> reads (CLAUDE.md §10b): files and folders anywhere, relative paths against
/// the folder mysync runs in. Walks like <see cref="FileScanner"/> (NFC names, symlinks and junctions
/// skipped and never entered, unreadable folders skipped, names no share path can hold skipped) but
/// applies no ignore rules: only mysync's own artifacts and OS clutter are left out, at any depth.
/// Never writes anything.
/// </summary>
internal sealed class LocalSources(ShareRoot current) : ILocalSources
{
    private const int BufferSize = 128 * 1024;

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public LocalSourceInfo CurrentFolder() => Describe(current.FullPath, current.FullPath);

    public LocalSourceInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Describe(path, Path.GetFullPath(path, current.FullPath));
    }

    public async Task<Result<IReadOnlyList<SyncedFolder>>> FindSyncedFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        List<SyncedFolder> found = [];

        for (DirectoryInfo? directory = new(Path.GetFullPath(folder)); directory is not null; directory = directory.Parent)
        {
            string state = Path.Combine(directory.FullName, StateFileNames.State);
            if (!File.Exists(state) && !Directory.Exists(state))
            {
                continue;
            }

            // Only the share ID is used; the state store's strict reading decides what is damaged.
            Result<ShareState?> loaded = await new JsonShareStateStore(new ShareRoot(directory.FullName)).LoadAsync(cancellationToken);
            if (loaded.IsFailure)
            {
                return Result.Failure<IReadOnlyList<SyncedFolder>>(loaded.Error);
            }

            if (loaded.Value is { } synced)
            {
                found.Add(new SyncedFolder(Path.TrimEndingDirectorySeparator(directory.FullName), synced.ShareId));
            }
        }

        return found;
    }

    public Task<Result<SourceListing>> ListAsync(IReadOnlyList<SourceToList> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Walk walk = new();

        foreach (SourceToList source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!source.IsFolder)
            {
                AddFile(new FileInfo(source.FullPath), source.Root!, walk);
                continue;
            }

            Result<int> inside = WalkDirectory(source.FullPath, source.Root, walk, cancellationToken);
            if (inside.IsFailure)
            {
                walk.Skipped.Add(new SkippedLocalPath(source.Root?.Value ?? ".", LocalSkipReason.Unreadable, inside.Error.Description));
            }
            else if (inside.Value == 0 && source.Root is { } root)
            {
                walk.EmptyFolders.Add(root);
            }
        }

        return Task.FromResult(Result.Success(new SourceListing(
            [.. walk.Files.OrderBy(file => file.Path, SharePath.OrdinalComparer)],
            [.. walk.EmptyFolders.Order(SharePath.OrdinalComparer)],
            [.. walk.Skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)])));
    }

    public async Task<Result<OpenedSourceFile>> OpenAsync(string fullPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream? stream = null;
        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            DateTime mtime = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
#pragma warning disable CA2000 // Ownership passes to the caller, which disposes the opened file.
            var opened = Result.Success(new OpenedSourceFile(stream, new DateTimeOffset(mtime, TimeSpan.Zero)));
#pragma warning restore CA2000
            stream = null;
            return opened;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result.Failure<OpenedSourceFile>(LocalFileErrors.Unreadable(fullPath, "it was removed before it could be read"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<OpenedSourceFile>(LocalFileErrors.Unreadable(fullPath, exception.Message));
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static LocalSourceInfo Describe(string argument, string fullPath)
    {
        string path = Path.TrimEndingDirectorySeparator(fullPath);
        string name = Path.GetFileName(path);

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        LocalSourceKind kind = info switch
        {
            { LinkTarget: not null } => LocalSourceKind.Symlink,
            DirectoryInfo => LocalSourceKind.Folder,
            FileInfo { Exists: true } => LocalSourceKind.File,
            _ => LocalSourceKind.Missing,
        };

        // A dangling symlink doesn't "exist", but it is there.
        if (kind == LocalSourceKind.Missing && new FileInfo(path).LinkTarget is not null)
        {
            kind = LocalSourceKind.Symlink;
        }

        return new LocalSourceInfo(argument, path, kind, PathRules.ToNfc(name), kind == LocalSourceKind.Folder && UnsafeRoots.IsUnsafe(path));
    }

    private static bool IsLeftOut(string name) =>
        StateFileNames.AlwaysIgnored.Contains(name, StringComparer.OrdinalIgnoreCase) || StateFileNames.IsOsClutter(name);

    private static void AddFile(FileInfo file, SharePath path, Walk walk)
    {
        if (IsLeftOut(file.Name))
        {
            return;
        }

        bool executable = !OperatingSystem.IsWindows() && (file.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        walk.Files.Add(new SourceFile(path, file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc), executable));
    }

    // Returns how many entries in the directory are pushed (so 0 means "empty").
    private static Result<int> WalkDirectory(string fullPath, SharePath? directory, Walk walk, CancellationToken cancellationToken)
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
            if (IsLeftOut(child.Name))
            {
                continue;
            }

            taking++;
            string name = PathRules.ToNfc(child.Name);
            string relative = directory is null ? name : $"{directory.Value}{SharePath.Separator}{name}";

            Result<SharePath> path = SharePath.Create(relative);
            if (path.IsFailure)
            {
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.InvalidName, path.Error.Description));
                continue;
            }

            if (!seen.Add(name))
            {
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.InvalidName, "another name in this folder is the same once Unicode-normalized"));
                continue;
            }

            if (child.LinkTarget is not null)
            {
                walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.Symlink, "symlinks and junctions are not pushed"));
                continue;
            }

            if (child is DirectoryInfo)
            {
                Result<int> inside = WalkDirectory(child.FullName, path.Value, walk, cancellationToken);
                if (inside.IsFailure)
                {
                    walk.Skipped.Add(new SkippedLocalPath(relative, LocalSkipReason.Unreadable, inside.Error.Description));
                }
                else if (inside.Value == 0)
                {
                    walk.EmptyFolders.Add(path.Value);
                }

                continue;
            }

            AddFile((FileInfo)child, path.Value, walk);
        }

        return taking;
    }

    private sealed class Walk
    {
        public List<SourceFile> Files { get; } = [];

        public List<SharePath> EmptyFolders { get; } = [];

        public List<SkippedLocalPath> Skipped { get; } = [];
    }
}
