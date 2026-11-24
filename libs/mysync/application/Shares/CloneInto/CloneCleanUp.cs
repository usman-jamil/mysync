using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.CloneInto;

/// <summary>
/// After a failed or cancelled <c>mysync clone</c>, like <c>git clone</c>: take away what the
/// clone wrote and the folders the command created, and nothing else.
/// </summary>
/// <remarks>
/// The target was empty when the clone started (only OS clutter and mysync's own artifacts), so a
/// file the clone wrote is one at a path of the share whose size and mtime are the manifest's: the
/// clone places every file with the manifest's mtime. Anything else (a file written meanwhile, the
/// same path with other content) is left where it is, with its folders, and reported.
/// </remarks>
internal sealed class CloneCleanUp(ICloneFolders folders)
{
    // Filesystems keep mtimes at different resolutions (FAT: 2 s).
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    public async Task<CloneCleanUpReport> RunAsync(string target, IReadOnlyList<string> namesBefore, IReadOnlyList<string> createdFolders, IReadOnlyList<Manifest> manifests)
    {
        Result<CloneFolderProbe> found = folders.Inspect(target);
        if (found.IsFailure || found.Value.Kind != CloneFolderKind.Folder)
        {
            // Gone already (or replaced by a file): only the parents this command created are left to remove.
            RemoveCreated(createdFolders, skipTarget: true);
            return new CloneCleanUpReport(target, FolderRemoved: found.IsSuccess && found.Value.Kind == CloneFolderKind.Missing, []);
        }

        List<(string Path, string Reason)> failures = [];
        ILocalFolder folder = folders.Open(target);

        await DeleteWrittenFilesAsync(folder, manifests, failures);
        await DeleteEmptiedDirectoriesAsync(folder, manifests);
        DeleteArtifacts(target, namesBefore, failures);

        bool removed = RemoveCreated(createdFolders, skipTarget: false);
        if (removed)
        {
            return new CloneCleanUpReport(target, FolderRemoved: true, [.. failures.Select(Describe)]);
        }

        // Still there: say what is in it that wasn't there before, besides what couldn't be deleted.
        HashSet<string> known = new(namesBefore, StringComparer.Ordinal);
        known.UnionWith(failures.Select(failure => failure.Path.Split(SharePath.Separator)[0]));
        Result<CloneFolderProbe> after = folders.Inspect(target);
        IEnumerable<string> others = after.IsSuccess
            ? after.Value.Names.Where(name => !known.Contains(name) && !StateFileNames.IsOsClutter(name)).Order(StringComparer.Ordinal)
            : [];

        return new CloneCleanUpReport(target, FolderRemoved: false, [.. failures.Select(Describe), .. others]);
    }

    // The reason names the path ("'a.txt' could not be written: …").
    private static string Describe((string Path, string Reason) failure) => failure.Reason;

    private static async Task DeleteWrittenFilesAsync(ILocalFolder folder, IReadOnlyList<Manifest> manifests, List<(string Path, string Reason)> failures)
    {
        ILookup<SharePath, FileEntry> files = manifests
            .SelectMany(manifest => manifest.Entries)
            .Where(entry => entry.Value is FileEntry)
            .ToLookup(entry => entry.Key, entry => (FileEntry)entry.Value);

        foreach (IGrouping<SharePath, FileEntry> path in files.OrderBy(group => group.Key, SharePath.OrdinalComparer))
        {
            Result<LocalFile?> local = await folder.GetFileAsync(path.Key, CancellationToken.None);
            if (local.IsFailure || local.Value is not { } file || !path.Any(entry => WrittenByTheClone(file, entry)))
            {
                continue; // not there, or not what the clone wrote: left alone
            }

            Result deleted = await folder.DeleteFileAsync(path.Key, CancellationToken.None);
            if (deleted.IsFailure)
            {
                failures.Add((path.Key.Value, deleted.Error.Description));
            }
        }
    }

    private static bool WrittenByTheClone(LocalFile file, FileEntry entry) =>
        file.Size == entry.Size && (file.MtimeUtc - entry.MtimeUtc).Duration() <= MtimeTolerance;

    // Folders of the share, deepest first, if nothing is left in them; a folder holding something
    // else stays, and so do its parents.
    private static async Task DeleteEmptiedDirectoriesAsync(ILocalFolder folder, IReadOnlyList<Manifest> manifests)
    {
        HashSet<SharePath> directories = [];
        foreach (KeyValuePair<SharePath, ManifestEntry> entry in manifests.SelectMany(manifest => manifest.Entries))
        {
            if (entry.Value is DirectoryEntry)
            {
                directories.Add(entry.Key);
            }

            for (SharePath? parent = entry.Key.Parent; parent is not null; parent = parent.Parent)
            {
                directories.Add(parent);
            }
        }

        foreach (SharePath directory in directories.OrderByDescending(path => path.Segments.Count).ThenBy(path => path.Value, StringComparer.Ordinal))
        {
            await folder.DeleteDirectoryIfEmptyAsync(directory, CancellationToken.None);
        }
    }

    // mysync's own files in the target, unless they were there before the clone.
    private void DeleteArtifacts(string target, IReadOnlyList<string> namesBefore, List<(string Path, string Reason)> failures)
    {
        foreach (string artifact in StateFileNames.AlwaysIgnored.Where(name => !namesBefore.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            Result deleted = folders.DeleteArtifact(Path.Combine(target, artifact));
            if (deleted.IsFailure)
            {
                failures.Add((artifact, deleted.Error.Description));
            }
        }
    }

    /// <returns>Whether the target is gone.</returns>
    private bool RemoveCreated(IReadOnlyList<string> createdFolders, bool skipTarget)
    {
        bool targetGone = false;
        for (int index = createdFolders.Count - 1; index >= 0; index--)
        {
            bool isTarget = index == createdFolders.Count - 1;
            if (isTarget && skipTarget)
            {
                continue;
            }

            Result<bool> deleted = folders.DeleteFolderIfEmpty(createdFolders[index]);
            if (deleted.IsFailure || !deleted.Value)
            {
                break; // something is in it: it stays, and so do the folders around it
            }

            targetGone |= isTarget;
        }

        return targetGone;
    }
}
