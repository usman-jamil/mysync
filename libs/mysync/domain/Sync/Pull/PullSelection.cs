using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;
using SharedKernel;

namespace MySync.Domain.Sync.Pull;

/// <summary>One share path a pull takes, and where it lands. Empty strings mean the share root and the destination root.</summary>
/// <param name="IsFile">A single file was named; otherwise a folder (or the whole share).</param>
public sealed record PullRoot(string SharePath, string LocalPath, bool IsFile = false);

/// <summary>A file to write: its entry in HEAD and its path relative to the destination.</summary>
public sealed record PullFile(SharePath SharePath, SharePath LocalPath, FileEntry Entry);

/// <summary>
/// What one <c>mysync pull</c> writes, worked out from one HEAD (CLAUDE.md §10c). It places things as
/// <c>push</c> places its sources: no paths → the share's contents at the destination root; a folder →
/// <c>&lt;dest&gt;/&lt;its name&gt;/…</c>; a file → <c>&lt;dest&gt;/&lt;its name&gt;</c>. With
/// <see cref="Latest"/>, each host's newest push folder lands at <c>&lt;dest&gt;/&lt;host&gt;/&lt;timestamp&gt;/…</c>.
/// The share's <c>.mysyncignore</c> is not applied: a pull takes exactly what was asked for.
/// </summary>
public sealed class PullSelection
{
    private PullSelection(IReadOnlyList<PullRoot> roots, IReadOnlyList<PullFile> files, IReadOnlyList<SharePath> emptyFolders)
    {
        Roots = roots;
        Files = files;
        EmptyFolders = emptyFolders;
    }

    public IReadOnlyList<PullRoot> Roots { get; }

    /// <summary>In ordinal order of their local path.</summary>
    public IReadOnlyList<PullFile> Files { get; }

    /// <summary>Folders the share records as empty (<c>dir</c> entries), relative to the destination.</summary>
    public IReadOnlyList<SharePath> EmptyFolders { get; }

    public bool IsEmpty => Files.Count == 0 && EmptyFolders.Count == 0;

    /// <param name="paths">Share paths; none means the whole share.</param>
    public static Result<PullSelection> Select(IReadOnlyDictionary<SharePath, ManifestEntry> entries, IReadOnlyList<SharePath> paths)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return Build(entries, [(null, null)]);
        }

        Dictionary<string, SharePath> landing = new(StringComparer.Ordinal);
        HashSet<SharePath> files = [];
        List<(SharePath?, SharePath?)> roots = [];
        foreach (SharePath path in paths)
        {
            bool isFile = entries.GetValueOrDefault(path) is FileEntry;
            if (!isFile && !IsFolder(entries, path))
            {
                return Result.Failure<PullSelection>(PullErrors.PathNotFound(path.Value));
            }

            if (landing.TryGetValue(path.Name, out SharePath? first))
            {
                return Result.Failure<PullSelection>(PullErrors.DuplicateTarget(path.Name, first.Value, path.Value));
            }

            landing.Add(path.Name, path);
            roots.Add((path, SharePath.Create(path.Name).Value));
            if (isFile)
            {
                files.Add(path);
            }
        }

        return Build(entries, roots, files);
    }

    /// <summary>
    /// <c>--latest</c>: for each host folder at the share root (one with a child named like a push
    /// folder, <see cref="PushTarget.TryParseFolderName(string, out DateTimeOffset, out int)"/>), its
    /// newest push folder. <paramref name="hosts"/> narrows that to those hosts, matched ignoring case.
    /// </summary>
    /// <param name="share">The share's name or ID, for <see cref="PullErrors.NoPushes"/>.</param>
    public static Result<PullSelection> Latest(IReadOnlyDictionary<SharePath, ManifestEntry> entries, IReadOnlyList<string> hosts, string share)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(share);

        Dictionary<string, (string Folder, DateTimeOffset Started, int Number)> newest = new(StringComparer.Ordinal);
        foreach ((SharePath path, ManifestEntry entry) in entries)
        {
            IReadOnlyList<string> segments = path.Segments;

            // A push folder is a folder: something below it, or an empty-folder entry.
            bool isFolder = segments.Count > 2 || segments.Count == 2 && entry is DirectoryEntry;
            if (!isFolder || !PushTarget.TryParseFolderName(segments[1], out DateTimeOffset started, out int number))
            {
                continue;
            }

            if (!newest.TryGetValue(segments[0], out (string Folder, DateTimeOffset Started, int Number) best) || (started, number).CompareTo((best.Started, best.Number)) > 0)
            {
                newest[segments[0]] = (segments[1], started, number);
            }
        }

        if (newest.Count == 0)
        {
            return Result.Failure<PullSelection>(PullErrors.NoPushes(share));
        }

        List<string> chosen = [.. newest.Keys];
        if (hosts.Count > 0)
        {
            if (hosts.FirstOrDefault(host => !newest.Keys.Any(name => string.Equals(name, host.Trim(), StringComparison.OrdinalIgnoreCase))) is { } missing)
            {
                return Result.Failure<PullSelection>(PullErrors.HostNotFound(missing));
            }

            chosen = [.. chosen.Where(name => hosts.Any(host => string.Equals(name, host.Trim(), StringComparison.OrdinalIgnoreCase)))];
        }

        List<(SharePath?, SharePath?)> roots = [.. chosen
            .Order(StringComparer.Ordinal)
            .Select(host => SharePath.Create($"{host}{SharePath.Separator}{newest[host].Folder}").Value)
            .Select(folder => ((SharePath?)folder, (SharePath?)folder))];
        return Build(entries, roots);
    }

    private static bool IsFolder(IReadOnlyDictionary<SharePath, ManifestEntry> entries, SharePath path) =>
        entries.GetValueOrDefault(path) is DirectoryEntry || entries.Keys.Any(key => IsUnder(key, path));

    // "web01/x" is under "web01"; "web012/x" is not.
    private static bool IsUnder(SharePath key, SharePath folder) =>
        key.Value.Length > folder.Value.Length
        && key.Value[folder.Value.Length] == SharePath.Separator
        && key.Value.StartsWith(folder.Value, StringComparison.Ordinal);

    /// <returns>Where <paramref name="path"/> is below <paramref name="root"/> (empty: the root itself), or <c>null</c> when it isn't.</returns>
    private static string? RelativeTo(SharePath? root, SharePath path)
    {
        if (root is null)
        {
            return path.Value;
        }

        if (path == root)
        {
            return string.Empty;
        }

        return IsUnder(path, root) ? path.Value[(root.Value.Length + 1)..] : null;
    }

    private static string Join(SharePath? local, string rest)
    {
        if (local is null)
        {
            return rest;
        }

        return rest.Length == 0 ? local.Value : $"{local.Value}{SharePath.Separator}{rest}";
    }

    /// <param name="roots">Each share path (null: the whole share) and where it lands (null: the destination root).</param>
    /// <param name="fileRoots">The roots that are single files.</param>
    private static PullSelection Build(IReadOnlyDictionary<SharePath, ManifestEntry> entries, IReadOnlyList<(SharePath? Share, SharePath? Local)> roots, HashSet<SharePath>? fileRoots = null)
    {
        List<PullFile> files = [];
        List<SharePath> empty = [];

        foreach ((SharePath? root, SharePath? local) in roots)
        {
            foreach ((SharePath path, ManifestEntry entry) in entries)
            {
                if (RelativeTo(root, path) is not { } rest)
                {
                    continue;
                }

                SharePath localPath = SharePath.Create(Join(local, rest)).Value;

                if (entry is FileEntry file)
                {
                    files.Add(new PullFile(path, localPath, file));
                }
                else
                {
                    empty.Add(localPath);
                }
            }
        }

        return new PullSelection(
            [.. roots.Select(root => new PullRoot(root.Share?.Value ?? string.Empty, root.Local?.Value ?? string.Empty, root.Share is { } share && fileRoots?.Contains(share) == true))],
            [.. files.OrderBy(file => file.LocalPath, SharePath.OrdinalComparer)],
            [.. empty.Distinct().Order(SharePath.OrdinalComparer)]);
    }
}
