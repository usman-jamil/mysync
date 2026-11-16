using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Sync;

/// <summary>What the sync does with one path.</summary>
public enum SyncActionKind
{
    /// <summary>Local and remote are equivalent (row 1). Only the base moves.</summary>
    NoChange = 0,

    // Remote wins (row 2): the local side becomes the remote entry.

    /// <summary>Download the remote file (replacing a local file or empty directory).</summary>
    Download = 1,

    /// <summary>Same bytes, different executable bit: change the local file mode only.</summary>
    UpdateLocalMode = 2,

    /// <summary>Create the remote's empty directory locally (replacing a local file if there is one).</summary>
    CreateLocalDirectory = 3,

    /// <summary>Delete the local file, or the local directory if it is empty.</summary>
    DeleteLocal = 4,

    // Local wins (row 3): the manifest takes the local entry.

    /// <summary>Upload the local file's bytes (if the object isn't stored yet) and record it.</summary>
    Upload = 5,

    /// <summary>Same bytes, different executable bit: change the manifest entry only.</summary>
    UpdateRemoteMode = 6,

    /// <summary>Record the local empty directory in the manifest.</summary>
    AddRemoteDirectory = 7,

    /// <summary>Remove the entry from the manifest.</summary>
    RemoveRemote = 8,

    /// <summary>Both sides changed differently (row 4, or a file/directory clash across paths). Dropped and logged.</summary>
    Conflict = 9,

    /// <summary>Not done this run (see <see cref="SkipReason"/>). Reconsidered next run.</summary>
    Skip = 10,
}

public enum SkipReason
{
    /// <summary><c>--no-delete</c>: the action would delete something, or depends on such a delete.</summary>
    NoDelete = 0,
}

/// <summary>
/// The decision for one path, with the three inputs it was made from (<c>null</c> = absent).
/// The <c>…Result</c> properties say what each side holds afterwards if the action is applied.
/// </summary>
public sealed record SyncAction(
    SharePath Path,
    SyncActionKind Kind,
    ManifestEntry? Base,
    ManifestEntry? Local,
    ManifestEntry? Remote,
    ConflictKind? ConflictKind = null,
    SkipReason? SkipReason = null)
{
    public bool IsRemoteWins => Kind is SyncActionKind.Download or SyncActionKind.UpdateLocalMode
        or SyncActionKind.CreateLocalDirectory or SyncActionKind.DeleteLocal;

    public bool IsLocalWins => Kind is SyncActionKind.Upload or SyncActionKind.UpdateRemoteMode
        or SyncActionKind.AddRemoteDirectory or SyncActionKind.RemoveRemote;

    /// <summary>The entry the committed manifest holds for this path afterwards.</summary>
    public ManifestEntry? RemoteResult => IsLocalWins ? Local : Remote;

    /// <summary>The entry the local folder holds for this path afterwards.</summary>
    public ManifestEntry? LocalResult => IsRemoteWins ? Remote : Local;

    /// <summary>
    /// The base afterwards. Conflicts and skips keep the old base, so the next run sees the
    /// change again; everything else records the agreed state.
    /// </summary>
    public ManifestEntry? NewBase => Kind switch
    {
        SyncActionKind.Conflict or SyncActionKind.Skip => Base,
        _ when IsRemoteWins => Remote,
        _ => Local,
    };

    /// <summary>Removes a local file or directory (including a file replaced by a directory).</summary>
    public bool DeletesLocally => Kind == SyncActionKind.DeleteLocal
        || Kind == SyncActionKind.CreateLocalDirectory && Local is FileEntry;

    /// <summary>Removes a manifest entry (including a file entry replaced by a directory).</summary>
    public bool DeletesRemotely => Kind == SyncActionKind.RemoveRemote
        || Kind == SyncActionKind.AddRemoteDirectory && Remote is FileEntry;
}
