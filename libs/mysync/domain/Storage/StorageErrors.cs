using SharedKernel;

namespace MySync.Domain.Storage;

/// <summary>
/// Provider-neutral storage failures (CLAUDE.md §11). Each provider maps its SDK's errors to
/// these. Descriptions never contain a connection string, account key or SAS token.
/// </summary>
public static class StorageErrors
{
    public static Error NotFound(string blobName) => Error.NotFound(
        "Storage.NotFound", $"'{blobName}' does not exist in the container.");

    public static Error PreconditionFailed(string blobName) => Error.Conflict(
        "Storage.PreconditionFailed", $"'{blobName}' was changed by someone else.");

    public static Error AlreadyExists(string blobName) => Error.Conflict(
        "Storage.AlreadyExists", $"'{blobName}' already exists.");

    /// <summary>The uploaded bytes don't hash to the object's name; nothing was stored.</summary>
    public static Error HashMismatch(string blobName) => Error.Problem(
        "Storage.HashMismatch", $"The content uploaded for '{blobName}' does not match its hash; it was not stored.");

    /// <summary>
    /// A stored object that can't be what its name says: its bytes, read back and decoded, don't hash
    /// to its name, its gzip is invalid or truncated, or it decodes to more than the manifest's size.
    /// Nothing decoded from it is used.
    /// </summary>
    public static Error CorruptObject(string blobName, string detail) => Error.Problem(
        "Storage.CorruptObject", $"The share's copy of '{blobName}' is damaged ({detail}); it was not used.");

    /// <summary>A stored object whose <c>Content-Encoding</c> this build can't decode. Never guessed.</summary>
    public static Error UnsupportedEncoding(string value) => Error.Problem(
        "Storage.UnsupportedEncoding", $"A stored file uses the encoding '{Content.ObjectEncodings.Display(value)}', which this mysync can't read. Upgrade mysync.");

    /// <summary>An alias blob whose share ID can't be read (missing or malformed metadata). Reported, not fatal.</summary>
    public static Error InvalidAlias(string name, string detail) => Error.Problem(
        "Storage.InvalidAlias", $"The name '{name}' can't be read ({detail}). Delete it with 'mysync delete {name} --yes' or rename its share.");

    /// <summary>A blob under aliases/ whose name isn't a share name. Skipped.</summary>
    public static Error StrayAlias(string blobName) => Error.Problem(
        "Storage.StrayAlias", $"'{blobName}' is under aliases/ but isn't a share name; it was skipped.");

    public static readonly Error Unauthorized = Error.Problem(
        "Storage.Unauthorized", "The storage account rejected the credentials. Check AZURE_STORAGE_CONNECTION_STRING.");

    /// <summary>The storage rejected the credentials or the request; <paramref name="hint"/> says what to check for that provider. Same code as <see cref="Unauthorized"/> (exit 8).</summary>
    public static Error UnauthorizedBecause(string hint) => Error.Problem(
        "Storage.Unauthorized", $"The storage rejected the request. {hint}");

    /// <summary>
    /// This machine's clock is too far from the storage service's for a signed request to be
    /// accepted (S3's <c>RequestTimeTooSkewed</c>). Not a credential problem.
    /// </summary>
    public static Error ClockSkew(int minutes) => Error.Problem(
        "Storage.ClockSkew",
        $"The storage service refused the request because this machine's clock is off by about {minutes} minute(s). Fix the clock (turn on NTP time sync) and run it again.");

    /// <summary>An object larger than the provider (or mysync's upload scheme for it) can store; nothing was stored.</summary>
    public static Error TooLarge(string blobName, long limitBytes) => Error.Problem(
        "Storage.TooLarge",
        $"'{blobName}' is larger than this storage can take in one object ({limitBytes:N0} bytes); it was not stored.");

    public static readonly Error Unreachable = Error.Problem(
        "Storage.Unreachable", "The storage account could not be reached. Check the network and the account name.");

    public static readonly Error Timeout = Error.Problem(
        "Storage.Timeout", "The storage request timed out.");
}
