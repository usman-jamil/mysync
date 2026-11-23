using System.Collections.Immutable;
using MySync.Application.Session;

namespace MySync.Tui.State;

/// <summary>
/// The main list as rows (CLAUDE.md §10d): the status entries that pass the filter and the search,
/// under their folders, folders first and then names, ordinal, as the manifest sorts. Folders carry
/// a rollup of what is pending below them; a collapsed folder hides its contents. Built once per
/// change of status, filter, search or collapsed folders, never per frame.
/// </summary>
public static class ListModel
{
    public static ImmutableArray<ListRow> Build(FolderStatus? status, ListFilter filter, string search, IReadOnlySet<string> collapsed)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        if (status is null)
        {
            return [];
        }

        Node root = new(string.Empty, string.Empty);
        foreach (FolderEntry entry in status.Entries)
        {
            if (Matches(entry, filter, search))
            {
                Add(root, entry);
            }
        }

        Aggregate(root);
        ImmutableArray<ListRow>.Builder rows = ImmutableArray.CreateBuilder<ListRow>();
        Emit(root, depth: 0, collapsed, rows);
        return rows.ToImmutable();
    }

    public static bool Matches(FolderEntry entry, ListFilter filter, string search)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool byFilter = filter switch
        {
            ListFilter.Here => entry.Status is FileStatus.NewHere or FileStatus.ChangedHere or FileStatus.DeletedHere,
            ListFilter.Share => entry.Status is FileStatus.NewInShare or FileStatus.ChangedInShare or FileStatus.DeletedInShare,
            ListFilter.Conflicts => entry.Status == FileStatus.Conflict,
            ListFilter.Other => entry.Status is FileStatus.Skipped or FileStatus.Ignored,
            ListFilter.Synced => entry.Status == FileStatus.Synced,
            _ => true,
        };

        return byFilter && (search.Length == 0 || entry.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How many entries each filter shows (the counts in the Show row).</summary>
    public static IReadOnlyDictionary<ListFilter, int> Counts(FolderStatus? status)
    {
        Dictionary<ListFilter, int> counts = Enum.GetValues<ListFilter>().ToDictionary(filter => filter, _ => 0);
        if (status is null)
        {
            return counts;
        }

        foreach (FolderEntry entry in status.Entries)
        {
            foreach (ListFilter filter in Enum.GetValues<ListFilter>())
            {
                if (Matches(entry, filter, string.Empty))
                {
                    counts[filter]++;
                }
            }
        }

        return counts;
    }

    private static void Add(Node root, FolderEntry entry)
    {
        string[] segments = entry.Path.Split('/');
        Node folder = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            folder = folder.Folder(segments[i]);
        }

        string name = segments[^1];
        if (entry.IsFolder)
        {
            folder.Folder(name).Entry = entry;
        }
        else
        {
            folder.Files[name] = entry;
        }
    }

    private static void Aggregate(Node node)
    {
        foreach (Node child in node.Folders.Values)
        {
            Aggregate(child);
            node.Changes += child.Changes;
            node.Conflicts += child.Conflicts;
            node.Skipped += child.Skipped;
            node.FileCount += child.FileCount;
            node.Bytes += child.Bytes;
        }

        foreach (FolderEntry file in node.Files.Values)
        {
            node.Changes += file.IsUpload || file.IsDownload || file.IsDelete ? 1 : 0;
            node.Conflicts += file.Status == FileStatus.Conflict ? 1 : 0;
            node.Skipped += file.Status == FileStatus.Skipped ? 1 : 0;
            node.FileCount += file.ExistsHere ? 1 : 0;
            node.Bytes += file.ExistsHere ? file.Size ?? 0 : 0;
        }
    }

    private static void Emit(Node node, int depth, IReadOnlySet<string> collapsed, ImmutableArray<ListRow>.Builder rows)
    {
        foreach (Node folder in node.Folders.Values)
        {
            bool expanded = !collapsed.Contains(folder.Path);
            rows.Add(new ListRow(folder.Path, folder.Name, depth, IsFolder: true, expanded, folder.Entry, folder.Changes, folder.Conflicts, folder.Skipped, folder.FileCount, folder.Bytes));
            if (expanded)
            {
                Emit(folder, depth + 1, collapsed, rows);
            }
        }

        foreach ((string name, FolderEntry file) in node.Files)
        {
            rows.Add(new ListRow(file.Path, name, depth, IsFolder: false, Expanded: false, file, Files: file.ExistsHere ? 1 : 0, Bytes: file.Size ?? 0));
        }
    }

    private sealed class Node(string name, string path)
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public FolderEntry? Entry { get; set; }

        public SortedDictionary<string, Node> Folders { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, FolderEntry> Files { get; } = new(StringComparer.Ordinal);

        public int Changes { get; set; }

        public int Conflicts { get; set; }

        public int Skipped { get; set; }

        public int FileCount { get; set; }

        public long Bytes { get; set; }

        public Node Folder(string segment)
        {
            if (!Folders.TryGetValue(segment, out Node? folder))
            {
                folder = new Node(segment, Path.Length == 0 ? segment : $"{Path}/{segment}");
                Folders.Add(segment, folder);
            }

            return folder;
        }
    }
}
