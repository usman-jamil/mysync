using System.Collections.Immutable;
using System.Text;
using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// A machine's disk as <c>mysync push</c> sees it: files and folders at absolute paths
/// (<c>/var/log/app/x.log</c>), with hooks for what busy servers do to files while they are read.
/// Listing follows the real <c>LocalSources</c>: NFC names, mysync's artifacts and OS clutter left
/// out at any depth, symlinks skipped, a folder whose entries are all left out counts as empty.
/// </summary>
public sealed class InMemoryLocalSources(IDateTimeProvider? clock = null) : ILocalSources
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = Node.Folder };
    private readonly Dictionary<string, ShareId?> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _opens = new(StringComparer.Ordinal);

    /// <summary>The folder push runs in.</summary>
    public string CurrentDirectory { get; set; } = "/srv/app";

    /// <summary>Folders that count as the home directory or a drive root.</summary>
    public HashSet<string> UnsafeRoots { get; } = new(StringComparer.Ordinal) { "/" };

    /// <summary>Files whose open fails as locked.</summary>
    public HashSet<string> Locked { get; } = new(StringComparer.Ordinal);

    /// <summary>Folders that can't be listed.</summary>
    public HashSet<string> UnreadableFolders { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before every open, with the path and how many times it was opened before: grow, shrink or remove a file here.</summary>
    public Action<string, int>? BeforeOpen { get; set; }

    /// <summary>Replaces the stream an open returns (e.g. one that ends early).</summary>
    public Func<string, byte[], Stream>? ReadStream { get; set; }

    /// <summary>How many times any file was opened.</summary>
    public int TotalOpens
    {
        get
        {
            lock (_gate)
            {
                return _opens.Values.Sum();
            }
        }
    }

    public int Opens(string fullPath)
    {
        lock (_gate)
        {
            return _opens.GetValueOrDefault(fullPath);
        }
    }

    public InMemoryLocalSources WriteFile(string fullPath, string content, bool executable = false, DateTimeOffset? mtimeUtc = null) =>
        WriteFile(fullPath, Encoding.UTF8.GetBytes(content), executable, mtimeUtc);

    public InMemoryLocalSources WriteFile(string fullPath, byte[] content, bool executable = false, DateTimeOffset? mtimeUtc = null)
    {
        lock (_gate)
        {
            AddParents(fullPath);
            _nodes[fullPath] = Node.File([.. content], mtimeUtc ?? Now(), executable);
        }

        return this;
    }

    public InMemoryLocalSources AppendToFile(string fullPath, string more)
    {
        lock (_gate)
        {
            Node node = _nodes[fullPath];
            _nodes[fullPath] = node with { Bytes = [.. node.Bytes, .. Encoding.UTF8.GetBytes(more)], MtimeUtc = Now() };
        }

        return this;
    }

    public InMemoryLocalSources AddFolder(string fullPath)
    {
        lock (_gate)
        {
            AddParents(fullPath);
            _nodes.TryAdd(fullPath, Node.Folder);
        }

        return this;
    }

    public InMemoryLocalSources AddSymlink(string fullPath)
    {
        lock (_gate)
        {
            AddParents(fullPath);
            _nodes[fullPath] = Node.Symlink;
        }

        return this;
    }

    /// <summary>A <c>.mysynctool</c> in <paramref name="folder"/> that records <paramref name="shareId"/> (or a damaged one, for <c>null</c>).</summary>
    public InMemoryLocalSources SyncedWith(string folder, ShareId? shareId)
    {
        WriteFile($"{folder.TrimEnd('/')}/{StateFileNames.State}", $"{{\"shareId\":\"{shareId?.ToString() ?? "damaged"}\"}}");
        lock (_gate)
        {
            _states[folder.TrimEnd('/')] = shareId;
        }

        return this;
    }

    public void Remove(string fullPath)
    {
        lock (_gate)
        {
            foreach (string path in _nodes.Keys.Where(path => path == fullPath || path.StartsWith(fullPath + "/", StringComparison.Ordinal)).ToList())
            {
                _nodes.Remove(path);
            }
        }
    }

    public bool Exists(string fullPath)
    {
        lock (_gate)
        {
            return _nodes.ContainsKey(fullPath);
        }
    }

    public byte[] ReadBytes(string fullPath)
    {
        lock (_gate)
        {
            return [.. _nodes[fullPath].Bytes];
        }
    }

    public LocalSourceInfo CurrentFolder() => Describe(CurrentDirectory, CurrentDirectory);

    public LocalSourceInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Describe(path, FullPath(path));
    }

    public Task<Result<IReadOnlyList<SyncedFolder>>> FindSyncedFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SyncedFolder> found = [];

        lock (_gate)
        {
            for (string? directory = folder; directory is not null; directory = Parent(directory))
            {
                if (_states.TryGetValue(directory, out ShareId? shareId))
                {
                    if (shareId is null)
                    {
                        return Task.FromResult(Result.Failure<IReadOnlyList<SyncedFolder>>(StateErrors.Corrupt("it isn't valid JSON")));
                    }

                    found.Add(new SyncedFolder(directory, shareId));
                }
            }
        }

        return Task.FromResult(Result.Success<IReadOnlyList<SyncedFolder>>(found));
    }

    public Task<Result<SourceListing>> ListAsync(IReadOnlyList<SourceToList> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        List<SourceFile> files = [];
        List<SharePath> empty = [];
        List<SkippedLocalPath> skipped = [];

        lock (_gate)
        {
            foreach (SourceToList source in sources)
            {
                if (!source.IsFolder)
                {
                    if (_nodes.TryGetValue(source.FullPath, out Node? node) && !IsLeftOut(Name(source.FullPath)))
                    {
                        files.Add(new SourceFile(source.Root!, source.FullPath, node.Bytes.Length, node.MtimeUtc, node.Executable));
                    }

                    continue;
                }

                int? taking = Walk(source.FullPath, source.Root, files, empty, skipped);
                if (taking is null)
                {
                    skipped.Add(new SkippedLocalPath(source.Root?.Value ?? ".", LocalSkipReason.Unreadable, "permission denied"));
                }
                else if (taking == 0 && source.Root is { } root)
                {
                    empty.Add(root);
                }
            }
        }

        return Task.FromResult(Result.Success(new SourceListing(
            [.. files.OrderBy(file => file.Path, SharePath.OrdinalComparer)],
            [.. empty.Order(SharePath.OrdinalComparer)],
            [.. skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)])));
    }

    public Task<Result<OpenedSourceFile>> OpenAsync(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int before;
        lock (_gate)
        {
            before = _opens.GetValueOrDefault(fullPath);
            _opens[fullPath] = before + 1;
        }

        BeforeOpen?.Invoke(fullPath, before);

        lock (_gate)
        {
            if (Locked.Contains(fullPath))
            {
                return Task.FromResult(Result.Failure<OpenedSourceFile>(LocalFileErrors.Unreadable(fullPath, "it is locked by another process")));
            }

            if (!_nodes.TryGetValue(fullPath, out Node? node) || node.Kind != NodeKind.File)
            {
                return Task.FromResult(Result.Failure<OpenedSourceFile>(LocalFileErrors.Unreadable(fullPath, "it was removed before it could be read")));
            }

            byte[] bytes = [.. node.Bytes];
            Stream stream = ReadStream?.Invoke(fullPath, bytes) ?? new MemoryStream(bytes, writable: false);
#pragma warning disable CA2000 // The caller owns the opened file.
            return Task.FromResult(Result.Success(new OpenedSourceFile(stream, node.MtimeUtc)));
#pragma warning restore CA2000
        }
    }

    // Callers hold _gate. Null: the folder can't be listed.
    private int? Walk(string folder, SharePath? relative, List<SourceFile> files, List<SharePath> empty, List<SkippedLocalPath> skipped)
    {
        if (UnreadableFolders.Contains(folder))
        {
            return null;
        }

        int taking = 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string path, Node node) in _nodes.Where(pair => Parent(pair.Key) == folder && pair.Key != "/").ToList())
        {
            string rawName = Name(path);
            if (IsLeftOut(rawName))
            {
                continue;
            }

            taking++;
            string name = PathRules.ToNfc(rawName);
            string display = relative is null ? name : $"{relative.Value}/{name}";
            Result<SharePath> shared = SharePath.Create(display);
            if (shared.IsFailure)
            {
                skipped.Add(new SkippedLocalPath(display, LocalSkipReason.InvalidName, shared.Error.Description));
                continue;
            }

            if (!seen.Add(name))
            {
                skipped.Add(new SkippedLocalPath(display, LocalSkipReason.InvalidName, "another name in this folder is the same once Unicode-normalized"));
                continue;
            }

            switch (node.Kind)
            {
                case NodeKind.Symlink:
                    skipped.Add(new SkippedLocalPath(display, LocalSkipReason.Symlink, "symlinks and junctions are not pushed"));
                    break;
                case NodeKind.Folder:
                    int? inside = Walk(path, shared.Value, files, empty, skipped);
                    if (inside is null)
                    {
                        skipped.Add(new SkippedLocalPath(display, LocalSkipReason.Unreadable, "permission denied"));
                    }
                    else if (inside == 0)
                    {
                        empty.Add(shared.Value);
                    }

                    break;
                default:
                    files.Add(new SourceFile(shared.Value, path, node.Bytes.Length, node.MtimeUtc, node.Executable));
                    break;
            }
        }

        return taking;
    }

    private LocalSourceInfo Describe(string argument, string fullPath)
    {
        lock (_gate)
        {
            LocalSourceKind kind = _nodes.TryGetValue(fullPath, out Node? node)
                ? node.Kind switch
                {
                    NodeKind.Folder => LocalSourceKind.Folder,
                    NodeKind.Symlink => LocalSourceKind.Symlink,
                    _ => LocalSourceKind.File,
                }
                : LocalSourceKind.Missing;

            return new LocalSourceInfo(argument, fullPath, kind, PathRules.ToNfc(Name(fullPath)), kind == LocalSourceKind.Folder && UnsafeRoots.Contains(fullPath));
        }
    }

    private string FullPath(string path)
    {
        string combined = path.StartsWith('/') ? path : $"{CurrentDirectory.TrimEnd('/')}/{path}";
        Stack<string> segments = new();
        foreach (string segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                segments.TryPop(out _);
            }
            else if (segment != ".")
            {
                segments.Push(segment);
            }
        }

        return "/" + string.Join('/', segments.Reverse());
    }

    // Callers hold _gate.
    private void AddParents(string fullPath)
    {
        for (string? parent = Parent(fullPath); parent is not null; parent = Parent(parent))
        {
            _nodes.TryAdd(parent, Node.Folder);
        }
    }

    private static bool IsLeftOut(string name) =>
        StateFileNames.AlwaysIgnored.Contains(name, StringComparer.OrdinalIgnoreCase) || StateFileNames.IsOsClutter(name);

    private static string? Parent(string path)
    {
        if (path == "/")
        {
            return null;
        }

        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Name(string path) => path == "/" ? string.Empty : path[(path.LastIndexOf('/') + 1)..];

    private DateTimeOffset Now() => clock is null ? new DateTimeOffset(2026, 11, 21, 5, 0, 0, TimeSpan.Zero) : new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);

    private enum NodeKind
    {
        File = 0,
        Folder = 1,
        Symlink = 2,
    }

    private sealed record Node(NodeKind Kind, ImmutableArray<byte> Bytes, DateTimeOffset MtimeUtc, bool Executable)
    {
        public static Node Folder { get; } = new(NodeKind.Folder, [], default, false);

        public static Node Symlink { get; } = new(NodeKind.Symlink, [], default, false);

        public static Node File(ImmutableArray<byte> bytes, DateTimeOffset mtime, bool executable) => new(NodeKind.File, bytes, mtime, executable);
    }
}
