using Microsoft.Extensions.Logging;

namespace MySync.Storage.Azure;

/// <summary>Debug-level detail for <c>--verbose</c>. Every message passes through <see cref="ConnectionStringRedactor"/> first.</summary>
internal static partial class AzureLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Azure request for {BlobName} failed with status {Status}: {Detail}")]
    public static partial void RequestFailed(ILogger logger, string blobName, int status, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upload of {BlobName} not committed: the content hashed to {Actual}")]
    public static partial void HashMismatch(ILogger logger, string blobName, string actual);
}
