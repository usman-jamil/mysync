using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// A local folder in memory: an <see cref="ILocalFolder"/> plus a simple <see cref="IFileScanner"/>
/// over the same tree, so handler tests never touch disk. Its <see cref="ILocalFolder"/> behaviour
/// is pinned by <see cref="Contracts.LocalFolderContractTests"/>, which also runs against the real
/// folder: if the two disagree, this fake is wrong.
/// </summary>
/// <remarks>
/// The scanner hashes every file (no hash cache reuse, no racy-mtime rule, no symlinks): those
/// belong to the real <c>FileScanner</c> and its own tests. Directories are explicit; writing a
/// file creates its parents. Thread-safe.
/// </remarks>
public sealed class InMemoryLocalFolder(IDateTimeProvider? clock = null) : ILocalFolder, IFileScanner
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<SharePath, Node> _nodes = new(SharePath.OrdinalComparer);
    private readonly HashSet<StagedFile> _staged = [];

    public string RootPath { get; init; } = "/memory/share";

    public bool IsUnsafeRoot { get; set; }

    /// <summary>
    /// False simulates Windows: the bit can't be read or set, so scans take it from the base and
    /// files are written without it.
    /// </summary>
    public bool ExecutableBitSupported { get; init; } = true;

    /// <summary>Simulates a case-insensitive filesystem (Windows, default macOS) for the pipeline's collision rule.</summary>
    public bool IsCaseSensitive { get; init; } = true;

    /// <summary>Simulates Windows for the pipeline's Windows-name rule.</summary>
    public bool RequiresWindowsCompatibleNames { get; init; }

    /// <summary>
    /// Runs before each operation on a path, outside the folder's lock. It may change the folder
    /// (to simulate an edit racing the sync) and may return an error to make that operation fail.
    /// </summary>
    public Func<FolderOperation, SharePath, Error?>? OnOperation { get; set; }

    /// <summary>Replaces the stream <see cref="OpenReadAsync"/> returns, e.g. with a <see cref="FailingStream"/> to simulate a file locked mid-upload.</summary>
    public Func<SharePath, byte[], Stream>? ReadStream { get; set; }

    /// <summary>
    /// Paths the scanner reports as skipped (unstable) instead of scanning. A directory here hides
    /// everything under it, as an unreadable directory does on disk.
    /// </summary>
    public HashSet<string> UnstablePaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Staged downloads not yet placed or disposed.</summary>
    public int StagedFileCount
    {
        get
        {
            lock (_gate)
            {
                return _staged.Count;
            }
        }
    }

    // Test setup and inspection

    public InMemoryLocalFolder WriteFile(string path, string content, bool executable = false, DateTimeOffset? mtimeUtc = null) =>
        WriteFile(path, Encoding.UTF8.GetBytes(content), executable, mtimeUtc);

    public InMemoryLocalFolder WriteFile(string path, byte[] content, bool executable = false, DateTimeOffset? mtimeUtc = null)
    {
        SharePath target = ManifestBuilder.PathOf(path);

        lock (_gate)
        {
            Unwrap(EnsureParents(target));
            _nodes[target] = new FileNode([.. content], mtimeUtc ?? Now(), executable && ExecutableBitSupported);
        }

        return this;
    }

    public InMemoryLocalFolder AddDirectory(string path)
    {
        lock (_gate)
        {
            Unwrap(CreateDirectory(ManifestBuilder.PathOf(path)));
        }

        return this;
    }

    public void Remove(string path)
    {
        SharePath target = ManifestBuilder.PathOf(path);

        lock (_gate)
        {
            foreach (SharePath key in _nodes.Keys.Where(key => key == target || key.IsUnder(target)).ToList())
            {
                _nodes.Remove(key);
            }
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return _nodes.ContainsKey(ManifestBuilder.PathOf(path));
        }
    }

    public bool IsDirectory(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(ManifestBuilder.PathOf(path), out Node? node) && node is DirectoryNode;
        }
    }

    public byte[] ReadBytes(string path) => [.. File(path).Bytes];

    public string ReadText(string path) => Encoding.UTF8.GetString(File(path).Bytes);

    public bool IsExecutable(string path) => File(path).Executable;

    public DateTimeOffset MtimeOf(string path) => File(path).MtimeUtc;

    /// <summary>Every path in the folder, sorted ordinally (directories included).</summary>
    public IReadOnlyList<string> AllPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _nodes.Keys.Select(key => key.Value)];
            }
        }
    }

    // ILocalFolder

    public Task<Result<IReadOnlyList<string>>> ListTopLevelNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<string> names = [.. _nodes.Keys.Where(key => key.Parent is null).Select(key => key.Value)];
            return Task.FromResult(Result.Success(names));
        }
    }

    public Task<Result<LocalFile?>> GetFileAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Intercept(FolderOperation.GetFile, path) is { } error)
        {
            return Task.FromResult(Result.Failure<LocalFile?>(error));
        }

        lock (_gate)
        {
            LocalFile? file = _nodes.TryGetValue(path, out Node? node) && node is FileNode f
                ? new LocalFile(path, f.Bytes.Length, f.MtimeUtc)
                : null;

            return Task.FromResult(Result.Success(file));
        }
    }

    public Task<Result<Stream>> OpenReadAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Intercept(FolderOperation.OpenRead, path) is { } error)
        {
            return Task.FromResult(Result.Failure<Stream>(error));
        }

        lock (_gate)
        {
            return Task.FromResult(_nodes.TryGetValue(path, out Node? node) && node is FileNode file
                ? Result.Success<Stream>(ReadStream?.Invoke(path, [.. file.Bytes]) ?? new MemoryStream([.. file.Bytes], writable: false))
                : Result.Failure<Stream>(LocalFileErrors.Unreadable(path.Value, "no such file")));
        }
    }

    public Task<Result<IStagedFile>> CreateStagedFileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StagedFile staged = new(this);

        lock (_gate)
        {
            _staged.Add(staged);
        }

        return Task.FromResult(Result.Success<IStagedFile>(staged));
    }

    public Task<Result> PlaceStagedFileAsync(
        IStagedFile staged, SharePath target, DateTimeOffset mtimeUtc, bool executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (staged is not StagedFile file || !ReferenceEquals(file.Owner, this))
        {
            throw new ArgumentException("Not a staged file of this folder.", nameof(staged));
        }

        if (Intercept(FolderOperation.PlaceStagedFile, target) is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        lock (_gate)
        {
            if (!_staged.Contains(file))
            {
                return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(target.Value, "the staged file is gone")));
            }

            if (_nodes.TryGetValue(target, out Node? existing) && existing is DirectoryNode && HasChildren(target))
            {
                return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(target.Value, "a non-empty directory is in the way")));
            }

            Result parents = EnsureParents(target);
            if (parents.IsFailure)
            {
                return Task.FromResult(parents);
            }

            _nodes[target] = new FileNode(file.Buffer.ToArray(), mtimeUtc.ToUniversalTime(), executable && ExecutableBitSupported);
            _staged.Remove(file);
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SetExecutableAsync(SharePath path, bool executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out Node? node) || node is not FileNode file)
            {
                return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(path.Value, "no such file")));
            }

            if (ExecutableBitSupported)
            {
                _nodes[path] = file with { Executable = executable };
            }

            return Task.FromResult(Result.Success());
        }
    }

    public Task<Result> CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(CreateDirectory(path));
        }
    }

    public Task<Result> DeleteFileAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Intercept(FolderOperation.DeleteFile, path) is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        lock (_gate)
        {
            if (_nodes.TryGetValue(path, out Node? node) && node is DirectoryNode)
            {
                return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(path.Value, "it is a directory")));
            }

            _nodes.Remove(path);
            return Task.FromResult(Result.Success());
        }
    }

    public Task<Result<bool>> DeleteDirectoryIfEmptyAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            bool delete = _nodes.TryGetValue(path, out Node? node) && node is DirectoryNode && !HasChildren(path);

            if (delete)
            {
                _nodes.Remove(path);
            }

            return Task.FromResult(Result.Success(delete));
        }
    }

    /// <summary>How many times staging was cleaned: a read-only command must leave this at 0.</summary>
    public int StagingCleans { get; private set; }

    public Task<Result> CleanStagingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StagingCleans++;

        lock (_gate)
        {
            _staged.Clear();
        }

        return Task.FromResult(Result.Success());
    }

    // IFileScanner

    public Task<Result<ScannedFolder>> ScanAsync(ScanRequest request, ISyncProgressReporter progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableSortedDictionary<SharePath, ManifestEntry>.Builder entries = ImmutableSortedDictionary.CreateBuilder<SharePath, ManifestEntry>(SharePath.OrdinalComparer);
        ImmutableSortedDictionary<SharePath, LocalFile>.Builder files = ImmutableSortedDictionary.CreateBuilder<SharePath, LocalFile>(SharePath.OrdinalComparer);
        ImmutableSortedDictionary<SharePath, HashCacheEntry>.Builder cache = ImmutableSortedDictionary.CreateBuilder<SharePath, HashCacheEntry>(SharePath.OrdinalComparer);

        List<SkippedLocalPath> skipped = [];

        lock (_gate)
        {
            foreach ((SharePath path, Node node) in _nodes)
            {
                if (IsIgnored(request, path, node is DirectoryNode))
                {
                    continue;
                }

                if (UnstablePaths.Any(skippedPath => path.IsUnder(ManifestBuilder.PathOf(skippedPath))))
                {
                    continue;
                }

                if (UnstablePaths.Contains(path.Value))
                {
                    skipped.Add(new SkippedLocalPath(path.Value, LocalSkipReason.Unstable, "it changed while it was being hashed"));
                    continue;
                }

                if (node is FileNode file)
                {
                    var hash = ContentHash.FromBytes(SHA256.HashData(file.Bytes));
                    bool executable = ExecutableBitSupported
                        ? file.Executable
                        : request.Base.GetValueOrDefault(path) is FileEntry { Executable: true };

                    entries[path] = Unwrap(FileEntry.Create(hash, file.Bytes.Length, file.MtimeUtc, executable));
                    files[path] = new LocalFile(path, file.Bytes.Length, file.MtimeUtc);
                    cache[path] = new HashCacheEntry(file.Bytes.Length, file.MtimeUtc.UtcTicks, hash);
                }
                else if (!HasChildren(path))
                {
                    entries[path] = DirectoryEntry.Instance;
                }
            }
        }

        return Task.FromResult(Result.Success(new ScannedFolder(entries.ToImmutable(), files.ToImmutable(), cache.ToImmutable(), [.. skipped])));
    }

    public Task<Result<FolderSummary>> SummarizeAsync(IIgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ignore);
        cancellationToken.ThrowIfCancellationRequested();
        ScanRequest request = new(
            ignore,
            ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
            ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer));

        lock (_gate)
        {
            List<FileNode> files = [.. _nodes
                .Where(pair => pair.Value is FileNode && !IsIgnored(request, pair.Key, isDirectory: false))
                .Select(pair => (FileNode)pair.Value)];

            return Task.FromResult(Result.Success(new FolderSummary(files.Count, files.Sum(file => (long)file.Bytes.Length))));
        }
    }

    // Internals (callers hold _gate, except Intercept)

    private Error? Intercept(FolderOperation operation, SharePath path) => OnOperation?.Invoke(operation, path);

    private static bool IsIgnored(ScanRequest request, SharePath path, bool isDirectory)
    {
        if (request.Ignore.IsIgnored(path, isDirectory))
        {
            return true;
        }

        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (request.Ignore.IsIgnored(ancestor, isDirectory: true))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasChildren(SharePath directory) => _nodes.Keys.Any(key => key.Parent == directory);

    private Result EnsureParents(SharePath path)
    {
        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (_nodes.TryGetValue(ancestor, out Node? node) && node is FileNode)
            {
                return Result.Failure(LocalFileErrors.WriteFailed(path.Value, $"'{ancestor.Value}' is a file"));
            }
        }

        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            _nodes.TryAdd(ancestor, DirectoryNode.Instance);
        }

        return Result.Success();
    }

    private Result CreateDirectory(SharePath path)
    {
        if (_nodes.TryGetValue(path, out Node? node))
        {
            return node is DirectoryNode
                ? Result.Success()
                : Result.Failure(LocalFileErrors.WriteFailed(path.Value, "a file is in the way"));
        }

        Result parents = EnsureParents(path);
        if (parents.IsSuccess)
        {
            _nodes[path] = DirectoryNode.Instance;
        }

        return parents;
    }

    private FileNode File(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(ManifestBuilder.PathOf(path), out Node? node) && node is FileNode file
                ? file
                : throw new InvalidOperationException($"No file at '{path}'.");
        }
    }

    private DateTimeOffset Now() => clock is null ? ManifestBuilder.DefaultTime : new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);

    private void Discard(StagedFile staged)
    {
        lock (_gate)
        {
            _staged.Remove(staged);
        }
    }

    private static void Unwrap(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }
    }

    private static T Unwrap<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Description);

    private abstract record Node;

    private sealed record FileNode(byte[] Bytes, DateTimeOffset MtimeUtc, bool Executable) : Node;

    private sealed record DirectoryNode : Node
    {
        public static DirectoryNode Instance { get; } = new();
    }

    private sealed class StagedFile(InMemoryLocalFolder owner) : IStagedFile
    {
        public InMemoryLocalFolder Owner => owner;

        public MemoryStream Buffer { get; } = new();

        public Stream Content => Buffer;

        public ValueTask DisposeAsync()
        {
            owner.Discard(this);
            Buffer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The operations <see cref="InMemoryLocalFolder.OnOperation"/> can intercept.</summary>
public enum FolderOperation
{
    GetFile = 0,
    OpenRead = 1,
    PlaceStagedFile = 2,
    DeleteFile = 3,
}
