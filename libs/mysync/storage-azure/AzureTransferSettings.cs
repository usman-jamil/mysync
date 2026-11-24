namespace MySync.Storage.Azure;

/// <summary>
/// How the store talks to Azure. Fixed in production; tests shrink the block size (to exercise
/// multi-block uploads with small files) and the retries (so "unreachable" fails fast).
/// </summary>
/// <param name="BlockSize">
/// Bytes per staged block. 4 MiB with Azure's 50,000-block limit allows ~195 GiB per file.
/// </param>
/// <param name="MaxRetries">The SDK's own retries (exponential back-off) for transient failures.</param>
/// <param name="NetworkTimeout">Per request, not per file: a large upload is many requests.</param>
/// <param name="CopyBlockSize">Bytes per block staged from a URL in a server-side copy (<see cref="AzureCopyLimits"/>).</param>
/// <param name="PutBlobFromUrlMaxBytes">Above this, a copy stages blocks from the URL instead of one Put Blob From URL; tests lower it to exercise the block path.</param>
internal sealed record AzureTransferSettings(
    int BlockSize,
    int MaxRetries,
    TimeSpan NetworkTimeout,
    long CopyBlockSize = AzureCopyLimits.BlockFromUrlSize,
    long PutBlobFromUrlMaxBytes = AzureCopyLimits.PutBlobFromUrlMaxBytes)
{
    public static AzureTransferSettings Default { get; } = new(4 * 1024 * 1024, 5, TimeSpan.FromSeconds(100));
}
