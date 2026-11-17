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

    public static Error InvalidManifest(string detail) => Error.Problem(
        "Shares.InvalidManifest",
        $"The share's manifest is invalid: {detail}");
}
