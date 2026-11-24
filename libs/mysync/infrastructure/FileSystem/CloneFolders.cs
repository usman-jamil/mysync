using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.Shares.Clone;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// <c>mysync clone</c>'s target folder on disk: what is there, creating it with its parents, and
/// removing again what the clone created. Relative paths start at <paramref name="current"/>.
/// </summary>
internal sealed class CloneFolders(ShareRoot current) : ICloneFolders
{
    public string CurrentFolder => current.FullPath;

    public Result<CloneFolderProbe> Inspect(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath, current.FullPath));
        string nearest = NearestExisting(path);

        if (Directory.Exists(path))
        {
            try
            {
                List<string> names = [.. Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry))];
                return new CloneFolderProbe(path, CloneFolderKind.Folder, names, path, UnsafeRoots.IsUnsafe(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<CloneFolderProbe>(LocalFileErrors.Unreadable(path, exception.Message));
            }
        }

        // A file, or a link or device that isn't a folder: not something to clone into.
        CloneFolderKind kind = File.Exists(path) || Path.Exists(path) ? CloneFolderKind.File : CloneFolderKind.Missing;
        return new CloneFolderProbe(path, kind, [], kind == CloneFolderKind.Missing ? nearest : path, IsUnsafeRoot: false);
    }

    public Result<IReadOnlyList<string>> Create(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath, current.FullPath));

        // The missing folders, outermost first.
        List<string> missing = [];
        for (string? folder = path; folder is not null && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            missing.Insert(0, folder);
        }

        List<string> created = [];
        foreach (string folder in missing)
        {
            try
            {
                if (File.Exists(folder) || Path.Exists(folder))
                {
                    throw new IOException($"'{Path.GetFileName(folder)}' is a file");
                }

                Directory.CreateDirectory(folder);
                created.Add(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                for (int index = created.Count - 1; index >= 0; index--)
                {
                    DeleteFolderIfEmpty(created[index]);
                }

                return Result.Failure<IReadOnlyList<string>>(CloneErrors.CannotCreate(path, exception.Message));
            }
        }

        return created;
    }

    public ILocalFolder Open(string fullPath) =>
        new LocalFolder(new ShareRoot(fullPath), StateFileNames.StagingDirectory, probeByWriting: false);

    public Result DeleteArtifact(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        try
        {
            if (PathResolver.IsLink(fullPath))
            {
                // The link itself, never what it points at.
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath);
                }
                else
                {
                    File.Delete(fullPath);
                }
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
            else if (File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
                File.Delete(fullPath);
            }

            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(LocalFileErrors.WriteFailed(Path.GetFileName(fullPath), exception.Message));
        }
    }

    public Result<bool> DeleteFolderIfEmpty(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        try
        {
            DirectoryInfo directory = new(fullPath);
            if (!directory.Exists)
            {
                return !File.Exists(fullPath);
            }

            if (directory.LinkTarget is not null)
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
                File.SetAttributes(clutter.FullName, FileAttributes.Normal);
                clutter.Delete();
            }

            directory.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still there: something appeared in it meanwhile, or it can't be removed. It stays.
            return Directory.Exists(fullPath) ? Result.Success(false) : Result.Failure<bool>(LocalFileErrors.WriteFailed(fullPath, exception.Message));
        }
    }

    private static string NearestExisting(string path)
    {
        for (string? folder = path; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder))
            {
                return folder;
            }
        }

        return Path.GetPathRoot(path) ?? path;
    }
}
