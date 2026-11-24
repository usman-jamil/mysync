using Microsoft.Extensions.Logging;

namespace MySync.Storage.S3;

/// <summary>Debug-level detail for <c>--verbose</c>. Every message passes through <see cref="S3Redactor"/> first.</summary>
internal static partial class S3Log
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 request for {Key} failed with status {Status} ({Code}): {Detail}")]
    public static partial void RequestFailed(ILogger logger, string key, int status, string code, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upload of {Key} not committed: the content hashed to {Actual}")]
    public static partial void HashMismatch(ILogger logger, string key, string actual);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 answered 409 ConditionalRequestConflict for {Key} (try {Attempt}); trying again")]
    public static partial void ConditionalConflict(ILogger logger, string key, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not abort the multipart upload of {Key}; S3 keeps its parts until a lifecycle rule removes them: {Detail}")]
    public static partial void AbortFailed(ILogger logger, string key, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolving AWS credentials failed: {Detail}")]
    public static partial void CredentialsFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not remove the publishing probe {Key}: {Detail}")]
    public static partial void ProbeNotRemoved(ILogger logger, string key, string detail);
}
