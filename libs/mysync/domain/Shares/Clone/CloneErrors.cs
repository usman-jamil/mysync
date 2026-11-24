using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Shares.Clone;

/// <summary>Why <c>mysync clone</c> stopped before downloading anything (CLAUDE.md §9, "<c>mysync clone</c>").</summary>
public static class CloneErrors
{
    /// <summary>A share given by ID that has no name: the folder can't be named after it. Exit 2.</summary>
    public static Error FolderRequired(ShareId shareId) => Error.Validation(
        "Clone.FolderRequired",
        $"This share has no name. Say where to clone it: mysync clone {shareId} <folder>");

    /// <summary>The target is a file, not a folder. Exit 4.</summary>
    public static Error TargetIsAFile(string path) => Error.Conflict(
        "Clone.TargetIsAFile",
        $"'{PathErrors.Display(path)}' is a file. Name a folder to clone into: mysync clone <share> <folder>");

    /// <summary>
    /// The target is, or is inside, a folder that syncs with a share: the clone would be uploaded as
    /// that share's files. Exit 4.
    /// </summary>
    /// <param name="folder">The folder with the <c>.mysynctool</c>.</param>
    /// <param name="share">The share it syncs with.</param>
    /// <param name="isTheTarget">The target itself is that folder (cloned before), not a folder inside it.</param>
    public static Error InsideAShare(string folder, ShareId share, bool isTheTarget = false) => Error.Conflict(
        "Clone.InsideAShare",
        isTheTarget
            ? $"'{PathErrors.Display(folder)}' already syncs with share {share}. Run 'mysync sync' in it, or clone somewhere else."
            : $"'{PathErrors.Display(folder)}' syncs with share {share}; a share cloned inside it would be uploaded as its files. Clone somewhere else.");

    /// <summary>The target folder (or a parent) can't be created. Exit 4.</summary>
    public static Error CannotCreate(string path, string reason) => Error.Problem(
        "Clone.CannotCreate",
        $"Can't create '{PathErrors.Display(path)}': {PathErrors.Display(reason)}");

    /// <summary>
    /// <see cref="ShareErrors.FolderNotEmpty"/> (same code, exit 4), with advice that fits <c>clone</c>,
    /// which has no <c>--force</c>.
    /// </summary>
    public static Error FolderNotEmpty(string folder) => Error.Conflict(
        "Shares.FolderNotEmpty",
        $"'{PathErrors.Display(folder)}' is not empty. Clone into a new or empty folder, or run 'mysync sync <share> --force' inside it to merge its files with the share.");

    /// <summary><see cref="ShareErrors.UnsafeRoot"/> (same code, exit 4), without the <c>--force</c> that <c>clone</c> doesn't have.</summary>
    public static Error UnsafeRoot(string folder) => Error.Problem(
        "Shares.UnsafeRoot",
        $"Refusing to clone into '{PathErrors.Display(folder)}' (home directory or drive root). Clone into a folder of its own: mysync clone <share> <folder>");
}
