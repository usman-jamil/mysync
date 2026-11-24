using SharedKernel;

namespace MySync.Domain.Shares.Clone;

/// <summary>
/// Where <c>mysync clone</c> puts a share: the folder given, or a new folder named after the share
/// inside the current one (like <c>git clone</c>). Pure: paths are only combined, never looked at.
/// </summary>
public static class CloneTarget
{
    /// <param name="share">What was typed for the share.</param>
    /// <param name="name">
    /// The share's name: the one typed, or, for a share given by ID, its first name in ordinal order
    /// (<c>null</c> if it has none, or if a folder was given and no name was looked up).
    /// </param>
    /// <param name="folder">The folder argument, absolute or relative to <paramref name="currentFolder"/>; <c>null</c> when not given.</param>
    /// <param name="currentFolder">The current folder, full.</param>
    /// <returns>The target's full path, without a trailing separator (a root keeps its own).</returns>
    public static Result<string> Choose(ShareReference share, ShareName? name, string? folder, string currentFolder)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentException.ThrowIfNullOrEmpty(currentFolder);

        if (folder is not null)
        {
            return Full(Path.GetFullPath(folder, currentFolder));
        }

        // Share names are a-z, 0-9 and '-': a valid folder name on every OS.
        ShareName? folderName = share.Name ?? name;
        return folderName is null
            ? Result.Failure<string>(CloneErrors.FolderRequired(share.Id!))
            : Full(Path.Combine(currentFolder, folderName.Value));
    }

    /// <summary>The first of a share's names in ordinal order (more than one after an interrupted rename), or <c>null</c>.</summary>
    public static ShareName? FirstName(IEnumerable<ShareName> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.OrderBy(name => name.Value, StringComparer.Ordinal).FirstOrDefault();
    }

    private static Result<string> Full(string path) => Path.TrimEndingDirectorySeparator(path);
}
