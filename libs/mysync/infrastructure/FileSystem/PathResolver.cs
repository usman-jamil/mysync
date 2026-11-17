using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// Maps a <see cref="SharePath"/> to a full path on disk, inside the share root (CLAUDE.md §8).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Manifest paths are NFC, but a name on disk may be NFD (macOS, or a Linux file named by a
/// Mac tool). Where the NFC spelling isn't on disk, a segment is matched by comparing each
/// directory entry's NFC form, so the existing file is found rather than a second one created.</item>
/// <item>A symlink or junction in the middle of a path is refused: following it could read or
/// write outside the share root.</item>
/// <item>On Windows, a name Windows can't hold is refused (<c>a:b</c> would write an alternate data
/// stream). The pipeline defers those first; this is the second line of defence.</item>
/// <item>The final full path is checked to be inside the root.</item>
/// </list>
/// </remarks>
internal sealed class PathResolver(ShareRoot root)
{
    private readonly string _rootPrefix = root.FullPath.EndsWith(Path.DirectorySeparatorChar)
        ? root.FullPath
        : root.FullPath + Path.DirectorySeparatorChar;

    public ShareRoot Root => root;

    /// <summary>The full path, or why it can't be used. Nothing is created.</summary>
    public Result<string> Resolve(SharePath path, Func<string, string, Error> error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(error);

        if (OperatingSystem.IsWindows() && PathRules.CheckWindowsCompatible(path) is { IsFailure: true } windows)
        {
            return Result.Failure<string>(error(path.Value, windows.Error.Description));
        }

        IReadOnlyList<string> segments = path.Segments;
        string current = root.FullPath;
        bool onDisk = true;

        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i];
            if (onDisk)
            {
                string? existing = FindOnDisk(current, segment);
                onDisk = existing is not null;
                current = existing ?? Path.Combine(current, segment);

                bool isLast = i == segments.Count - 1;
                if (onDisk && !isLast && IsLink(current))
                {
                    return Result.Failure<string>(error(path.Value, "a symlink or junction is in the way"));
                }
            }
            else
            {
                current = Path.Combine(current, segment);
            }
        }

        string full = Path.GetFullPath(current);
        return full.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? full
            : Result.Failure<string>(error(path.Value, "it resolves outside the share folder"));
    }

    public static bool IsLink(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The entry in `directory` whose name is `segment` once normalized to NFC, or null.
    private static string? FindOnDisk(string directory, string segment)
    {
        string direct = Path.Combine(directory, segment);
        if (Path.Exists(direct) || IsLink(direct))
        {
            return direct;
        }

        // Only a name with non-ASCII characters can be spelled differently on disk.
        if (!Directory.Exists(directory) || segment.All(char.IsAscii))
        {
            return null;
        }

        try
        {
            string? match = null;
            foreach (string candidate in Directory.EnumerateFileSystemEntries(directory))
            {
                if (string.Equals(PathRules.ToNfc(Path.GetFileName(candidate)), segment, StringComparison.Ordinal))
                {
                    match = candidate;
                    break;
                }
            }

            return match;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
