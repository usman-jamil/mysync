using System.Collections.Concurrent;
using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// The synced folder on disk (CLAUDE.md §8, §9 phase 8). Every <see cref="SharePath"/> goes through
/// <see cref="PathResolver"/>, so nothing is read or written outside the share root. Downloads are
/// staged in <c>.mysynctool.tmp/</c> (same volume), given their mtime and mode there, and then
/// renamed into place, so a file only ever appears complete.
/// </summary>
/// <remarks>
/// <see cref="DeleteDirectoryIfEmptyAsync"/> treats a directory holding only OS clutter
/// (<c>.DS_Store</c>, <c>Thumbs.db</c>, <c>desktop.ini</c>, <c>._*</c>) as empty and removes the
/// clutter with it; otherwise browsing a folder in Finder would keep it from ever being deleted.
/// </remarks>
internal sealed class LocalFolder : ILocalFolder
{
    private const int BufferSize = 128 * 1024;

    private const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly PathResolver _paths;
    private readonly string _staging;
    private readonly Lazy<bool> _caseSensitive;
    private readonly Lazy<bool> _unsafeRoot;
    private readonly ConcurrentDictionary<StagedFile, byte> _liveStaged = new();

    public LocalFolder(ShareRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _paths = new PathResolver(root);
        _staging = root.Combine(StateFileNames.StagingDirectory);
        _caseSensitive = new Lazy<bool>(ProbeCaseSensitivity);
        _unsafeRoot = new Lazy<bool>(() => UnsafeRoots.IsUnsafe(root.FullPath));
    }

    public string RootPath => _paths.Root.FullPath;

    public bool IsUnsafeRoot => _unsafeRoot.Value;

    public bool IsCaseSensitive => _caseSensitive.Value;

    public bool RequiresWindowsCompatibleNames => OperatingSystem.IsWindows();

    public Task<Result<IReadOnlyList<string>>> ListTopLevelNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Attempt<IReadOnlyList<string>>(".", LocalFileErrors.Unreadable, () =>
            [.. Directory.EnumerateFileSystemEntries(RootPath).Select(entry => PathRules.ToNfc(Path.GetFileName(entry)))]));
    }

    public Task<Result<LocalFile?>> GetFileAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.Unreadable);
        if (full.IsFailure)
        {
            return Task.FromResult(Result.Failure<LocalFile?>(full.Error));
        }

        return Task.FromResult(Attempt(path.Value, LocalFileErrors.Unreadable, () =>
        {
            FileInfo info = new(full.Value);
            return info.Exists && info.LinkTarget is null
                ? new LocalFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc))
                : (LocalFile?)null;
        }));
    }

    public Task<Result<Stream>> OpenReadAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.Unreadable);
        if (full.IsFailure)
        {
            return Task.FromResult(Result.Failure<Stream>(full.Error));
        }

        if (PathResolver.IsLink(full.Value))
        {
            return Task.FromResult(Result.Failure<Stream>(LocalFileErrors.Unreadable(path.Value, "it is a symlink")));
        }

        // Others may keep writing; the upload's hash check catches a change.
        return Task.FromResult(Attempt<Stream>(path.Value, LocalFileErrors.Unreadable, () => new FileStream(
            full.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan)));
    }

    public Task<Result<IStagedFile>> CreateStagedFileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Attempt<IStagedFile>(StateFileNames.StagingDirectory, LocalFileErrors.WriteFailed, () =>
        {
            Directory.CreateDirectory(_staging);
            string file = Path.Combine(_staging, $"{Guid.NewGuid():N}.part");
            StagedFile staged = new(this, file, new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous));
            _liveStaged[staged] = 0;
            return staged;
        }));
    }

    public async Task<Result> PlaceStagedFileAsync(
        IStagedFile staged, SharePath target, DateTimeOffset mtimeUtc, bool executable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (staged is not StagedFile file || !ReferenceEquals(file.Owner, this))
        {
            throw new ArgumentException("Not a staged file of this folder.", nameof(staged));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_liveStaged.ContainsKey(file))
        {
            return Result.Failure(LocalFileErrors.WriteFailed(target.Value, "the staged file is gone"));
        }

        Result<string> full = _paths.Resolve(target, LocalFileErrors.WriteFailed);
        if (full.IsFailure)
        {
            return full;
        }

        await file.CloseAsync();

        return Attempt(target.Value, LocalFileErrors.WriteFailed, () =>
        {
            if (!File.Exists(file.FilePath))
            {
                return LocalFileErrors.WriteFailed(target.Value, "the staged file is gone");
            }

            if (Directory.Exists(full.Value) && !PathResolver.IsLink(full.Value))
            {
                if (Directory.EnumerateFileSystemEntries(full.Value).Any())
                {
                    return LocalFileErrors.WriteFailed(target.Value, "a non-empty directory is in the way");
                }

                Directory.Delete(full.Value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full.Value)!);

            // The file gets its final mtime and mode before it appears.
            File.SetLastWriteTimeUtc(file.FilePath, mtimeUtc.UtcDateTime);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file.FilePath, WithExecute(File.GetUnixFileMode(file.FilePath), executable));
            }

            ClearReadOnly(full.Value);
            File.Move(file.FilePath, full.Value, overwrite: true);
            _liveStaged.TryRemove(file, out _);
            file.MarkPlaced();
            return null;
        });
    }

    public Task<Result> SetExecutableAsync(SharePath path, bool executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.WriteFailed);
        if (full.IsFailure)
        {
            return Task.FromResult<Result>(full);
        }

        return Task.FromResult(Attempt(path.Value, LocalFileErrors.WriteFailed, () =>
        {
            if (!File.Exists(full.Value) || PathResolver.IsLink(full.Value))
            {
                return LocalFileErrors.WriteFailed(path.Value, "no such file");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(full.Value, WithExecute(File.GetUnixFileMode(full.Value), executable));
            }

            return null;
        }));
    }

    public Task<Result> CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.WriteFailed);
        if (full.IsFailure)
        {
            return Task.FromResult<Result>(full);
        }

        return Task.FromResult(Attempt(path.Value, LocalFileErrors.WriteFailed, () =>
        {
            if (File.Exists(full.Value) || PathResolver.IsLink(full.Value))
            {
                return LocalFileErrors.WriteFailed(path.Value, "a file is in the way");
            }

            Directory.CreateDirectory(full.Value);
            return null;
        }));
    }

    public Task<Result> DeleteFileAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.WriteFailed);
        if (full.IsFailure)
        {
            return Task.FromResult<Result>(full);
        }

        return Task.FromResult(Attempt(path.Value, LocalFileErrors.WriteFailed, () =>
        {
            if (Directory.Exists(full.Value) && !PathResolver.IsLink(full.Value))
            {
                return LocalFileErrors.WriteFailed(path.Value, "it is a directory");
            }

            ClearReadOnly(full.Value);
            File.Delete(full.Value); // a missing file is not an error
            return null;
        }));
    }

    public Task<Result<bool>> DeleteDirectoryIfEmptyAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> full = _paths.Resolve(path, LocalFileErrors.WriteFailed);
        if (full.IsFailure)
        {
            return Task.FromResult(Result.Failure<bool>(full.Error));
        }

        return Task.FromResult(Attempt(path.Value, LocalFileErrors.WriteFailed, () =>
        {
            DirectoryInfo directory = new(full.Value);
            if (!directory.Exists || directory.LinkTarget is not null)
            {
                return false;
            }

            List<FileSystemInfo> children = [.. directory.EnumerateFileSystemInfos()];
            if (!children.TrueForAll(child => child is FileInfo { LinkTarget: null } && StateFileNames.IsOsClutter(child.Name)))
            {
                return false;
            }

            foreach (FileSystemInfo clutter in children)
            {
                ClearReadOnly(clutter.FullName);
                clutter.Delete();
            }

            try
            {
                directory.Delete();
                return true;
            }
            catch (IOException) when (Directory.Exists(full.Value))
            {
                return false; // something appeared in it meanwhile
            }
        }));
    }

    public Task<Result> CleanStagingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (StagedFile staged in _liveStaged.Keys)
        {
            _liveStaged.TryRemove(staged, out _);
            staged.Discard();
        }

        return Task.FromResult(Attempt(StateFileNames.StagingDirectory, LocalFileErrors.WriteFailed, () =>
        {
            if (PathResolver.IsLink(_staging))
            {
                Directory.Delete(_staging); // the link itself, never what it points at
            }
            else if (Directory.Exists(_staging))
            {
                Directory.Delete(_staging, recursive: true);
            }

            return null;
        }));
    }

    internal void Forget(StagedFile staged) => _liveStaged.TryRemove(staged, out _);

    private static UnixFileMode WithExecute(UnixFileMode mode, bool executable)
    {
        if (!executable)
        {
            return mode & ~AnyExecute;
        }

        // Like chmod +x under a usual umask: execute for whoever may read.
        UnixFileMode execute = UnixFileMode.UserExecute;
        if (mode.HasFlag(UnixFileMode.GroupRead))
        {
            execute |= UnixFileMode.GroupExecute;
        }

        if (mode.HasFlag(UnixFileMode.OtherRead))
        {
            execute |= UnixFileMode.OtherExecute;
        }

        return mode | execute;
    }

    // Windows refuses to replace or delete a read-only file.
    private static void ClearReadOnly(string fullPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(fullPath))
        {
            FileAttributes attributes = File.GetAttributes(fullPath);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private bool ProbeCaseSensitivity()
    {
        // Ask the filesystem itself: macOS volumes can be either, and so can Linux mounts.
        try
        {
            Directory.CreateDirectory(_staging);
            string probe = Path.Combine(_staging, $"case-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            try
            {
                return !File.Exists(Path.Combine(_staging, Path.GetFileName(probe).ToUpperInvariant()));
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (IOException)
        {
            return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        }
        catch (UnauthorizedAccessException)
        {
            return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        }
    }

    private static Result<T> Attempt<T>(string path, Func<string, string, Error> error, Func<T> action)
    {
        try
        {
            return Result.Success(action()); // explicit: the implicit conversion turns null into a failure
        }
        catch (IOException exception)
        {
            return Result.Failure<T>(error(path, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<T>(error(path, exception.Message));
        }
    }

    private static Result Attempt(string path, Func<string, string, Error> error, Func<Error?> action)
    {
        Result<Error?> outcome = Attempt<Error?>(path, error, action);
        if (outcome.IsFailure)
        {
            return outcome;
        }

        return outcome.Value is { } failure ? Result.Failure(failure) : Result.Success();
    }

    /// <summary>A download being written into <c>.mysynctool.tmp/</c>.</summary>
    internal sealed class StagedFile(LocalFolder owner, string filePath, FileStream stream) : IStagedFile
    {
        private int _closed;
        private bool _placed;

        public LocalFolder Owner => owner;

        public string FilePath => filePath;

        public Stream Content => stream;

        public async ValueTask CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                await stream.DisposeAsync();
            }
        }

        public void MarkPlaced() => _placed = true;

        public void Discard()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                stream.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            owner.Forget(this);
            if (!_placed)
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException)
                {
                    // Cleaned at the start and end of every run anyway.
                }
                catch (UnauthorizedAccessException)
                {
                    // As above.
                }
            }
        }
    }
}
