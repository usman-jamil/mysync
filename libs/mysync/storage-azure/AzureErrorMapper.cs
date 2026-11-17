using System.Net.Sockets;
using Azure;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.Azure;

/// <summary>
/// Azure SDK failures → <see cref="StorageErrors"/> (CLAUDE.md §11). Nothing above this project
/// sees an Azure exception, and no error built here carries SDK text: SDK messages can echo
/// request details, so they only reach the debug log, redacted.
/// </summary>
internal static class AzureErrorMapper
{
    /// <summary>
    /// Whether this is a failure the store reports as a <see cref="Result"/> rather than letting
    /// it propagate. The caller's own cancellation always propagates.
    /// </summary>
    public static bool IsStorageFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested, // a network timeout, not the user
        RequestFailedException or HttpRequestException or SocketException or TimeoutException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(inner => IsStorageFailure(inner, cancellationToken)),
        _ => false,
    };

    public static Error Map(Exception exception, string blobName) => exception switch
    {
        RequestFailedException failed => FromStatus(failed.Status, blobName),
        AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => Map(aggregate.InnerExceptions[^1], blobName), // the SDK's "retry failed after n tries"
        OperationCanceledException or TimeoutException => StorageErrors.Timeout,
        _ => StorageErrors.Unreachable,
    };

    private static Error FromStatus(int status, string blobName) => status switch
    {
        401 or 403 => StorageErrors.Unauthorized,
        404 => StorageErrors.NotFound(blobName),
        408 => StorageErrors.Timeout,
        409 => StorageErrors.AlreadyExists(blobName),
        412 => StorageErrors.PreconditionFailed(blobName),
        _ => StorageErrors.Unreachable, // 0 (no response), 5xx after the SDK's retries
    };

    /// <summary>The HTTP status, 0 when no response arrived.</summary>
    public static int StatusOf(Exception exception) => exception switch
    {
        RequestFailedException failed => failed.Status,
        AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => StatusOf(aggregate.InnerExceptions[^1]),
        _ => 0,
    };
}
