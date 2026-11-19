using SharedKernel;

namespace MySync.Domain.Shares;

public static class ShareErrors
{
    // Never echoes the input: someone may paste a connection string where the ID belongs.
    public static Error InvalidId() => Error.Validation(
        "Shares.InvalidId",
        "The share ID must be a UUID such as 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10.");

    public static Error NotFound(ShareId shareId) => Error.NotFound(
        "Shares.NotFound",
        $"Share {shareId} was not found in this storage container.");

    public static Error FolderNotEmpty(string folder) => Error.Conflict(
        "Shares.FolderNotEmpty",
        $"'{folder}' is not empty. Clone into an empty folder, or pass --force.");

    /// <summary>
    /// The share was deleted (<c>mysync delete</c>). Every command that needs the share stops here,
    /// and leaves this folder's files alone. Exit 8, like a share that doesn't exist.
    /// </summary>
    public static Error Deleted(ShareId shareId, Manifests.ShareDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(deletion);

        return Error.NotFound(
            "Shares.Deleted",
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Share {shareId} was deleted by {Paths.PathErrors.Display(deletion.DeletedBy)} on {deletion.DeletedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Nothing was changed here. Run 'mysync delete --yes' in this folder to make it an ordinary folder again; your files are kept."));
    }

    public static Error AlreadyAShare(ShareId existingShareId) => Error.Conflict(
        "Shares.AlreadyAShare",
        $"This folder is already share {existingShareId}. Run 'mysync sync' without an ID.");

    public static Error NotAShare() => Error.Problem(
        "Shares.NotAShare",
        "This folder is not a share yet. Run 'mysync sync' to create one, or 'mysync sync <id>' to clone one.");

    public static Error UnsafeRoot(string folder) => Error.Problem(
        "Shares.UnsafeRoot",
        $"Refusing to share '{folder}' (home directory or drive root). Pass --force if you really mean it.");

    public static Error UnsupportedFormat(int formatVersion) => Error.Problem(
        "Shares.UnsupportedFormat",
        $"This share uses manifest format {formatVersion}, which is newer than this mysync supports. Upgrade mysync.");

    /// <summary>A new share needs a name (<c>--name</c>); the suggestion comes from the folder's name.</summary>
    public static Error NameRequired(ShareName? suggestion) => Error.Validation(
        "Shares.NameRequired",
        $"A new share needs a name: mysync sync --name {suggestion?.Value ?? "<name>"}");

    // Never echoes the input, like InvalidId.
    public static Error InvalidName(string reason) => Error.Validation(
        "Shares.InvalidName",
        $"That isn't a valid share name: {reason}. A name is 3-40 letters a-z, digits and '-', starting and ending with a letter or digit.");

    /// <summary>What's typed where a share is expected is neither a share ID nor a valid name.</summary>
    public static Error InvalidReference() => Error.Validation(
        "Shares.InvalidReference",
        "Name a share by its name (3-40 letters a-z, digits and '-') or its share ID (a UUID). 'mysync shares' lists them.");

    public static Error NameTaken(ShareName name, ShareId existingShareId) => Error.Conflict(
        "Shares.NameTaken",
        $"The name '{name}' is already taken by share {existingShareId}. Pick another name.");

    public static Error NameNotFound(ShareName name) => Error.NotFound(
        "Shares.NameNotFound",
        $"No share is named '{name}'. 'mysync shares' lists the names.");

    /// <summary>The name was claimed, but its share was never finished (an interrupted create).</summary>
    public static Error Incomplete(ShareName name, ShareId shareId) => Error.NotFound(
        "Shares.Incomplete",
        $"The share behind '{name}' ({shareId}) was never finished. 'mysync delete {name} --yes' removes it and frees the name.");

    public static Error InvalidManifest(string detail) => Error.Problem(
        "Shares.InvalidManifest",
        $"The share's manifest is invalid: {detail}");
}
