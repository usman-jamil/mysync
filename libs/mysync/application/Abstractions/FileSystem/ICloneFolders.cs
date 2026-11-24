using SharedKernel;

namespace MySync.Application.Abstractions.FileSystem;

/// <summary>
/// The folders around <c>mysync clone</c>'s target (CLAUDE.md §9, "<c>mysync clone</c>"): what is
/// there, creating it with its parents, and taking away again only what the clone created. Not
/// bound to the share root; paths are full. Implemented in Infrastructure.
/// </summary>
public interface ICloneFolders
{
    /// <summary>The folder mysync runs in, full: relative folder arguments start here.</summary>
    string CurrentFolder { get; }

    /// <summary>What is at <paramref name="fullPath"/>. Fails only when a folder there can't be listed.</summary>
    Result<CloneFolderProbe> Inspect(string fullPath);

    /// <summary>
    /// Creates <paramref name="fullPath"/> and any missing parents. Returns the folders it created,
    /// outermost first (empty when the folder existed). <c>Clone.CannotCreate</c> on failure, after
    /// removing whatever it had created.
    /// </summary>
    Result<IReadOnlyList<string>> Create(string fullPath);

    /// <summary>The folder at <paramref name="fullPath"/> as an <see cref="ILocalFolder"/> (the same path rules as a share root), for the clean-up.</summary>
    ILocalFolder Open(string fullPath);

    /// <summary>Deletes one of mysync's own artifacts (a file, or the staging directory and what is in it). Missing is success.</summary>
    Result DeleteArtifact(string fullPath);

    /// <summary>
    /// Deletes a folder if nothing but OS clutter is in it (the clutter goes with it, as in a sync).
    /// Returns whether it is gone; a missing folder counts as gone.
    /// </summary>
    Result<bool> DeleteFolderIfEmpty(string fullPath);
}

/// <summary>What <see cref="ICloneFolders.Inspect"/> found.</summary>
public enum CloneFolderKind
{
    Missing,
    Folder,

    /// <summary>A file, or anything else that isn't a folder.</summary>
    File,
}

/// <param name="FullPath">The path inspected.</param>
/// <param name="Kind">What is there.</param>
/// <param name="Names">For a folder, the names directly in it; otherwise empty.</param>
/// <param name="NearestExisting">The path itself when it exists, else its nearest existing parent.</param>
/// <param name="IsUnsafeRoot">The home folder, a drive or volume root, or <c>/</c>.</param>
public sealed record CloneFolderProbe(string FullPath, CloneFolderKind Kind, IReadOnlyList<string> Names, string NearestExisting, bool IsUnsafeRoot);
