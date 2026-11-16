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

    public static readonly Error Unauthorized = Error.Problem(
        "Storage.Unauthorized", "The storage account rejected the credentials. Check AZURE_STORAGE_CONNECTION_STRING.");

    public static readonly Error Unreachable = Error.Problem(
        "Storage.Unreachable", "The storage account could not be reached. Check the network and the account name.");

    public static readonly Error Timeout = Error.Problem(
        "Storage.Timeout", "The storage request timed out.");
}
