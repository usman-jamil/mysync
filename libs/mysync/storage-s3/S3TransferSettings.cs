namespace MySync.Storage.S3;

/// <summary>
/// How the S3 stores talk to S3. Fixed in production; tests shrink the part size (S3 still needs
/// 5 MiB parts, so the real-bucket tests use 5 MiB), the copy threshold (to exercise the multipart
/// copy without a 5 GiB object) and the retries.
/// </summary>
/// <param name="PartSize">The smallest part of a multipart upload, and the single-PutObject cut-off.</param>
/// <param name="MaxRetries">The SDK's own retries (exponential back-off) for transient failures: 5xx, SlowDown, timeouts.</param>
/// <param name="Timeout">Per request, not per file: a large upload is many requests.</param>
/// <param name="ConditionalConflictAttempts">
/// Tries of one conditional write that S3 answers with 409 ConditionalRequestConflict (a concurrent
/// request got in the way, outcome undecided), before the write is reported <c>Storage.Unreachable</c>.
/// </param>
/// <param name="ConflictBaseDelay">The first pause after a 409; doubles each try, with jitter.</param>
/// <param name="CopyObjectMaxBytes">Above this, a publish copy is a multipart copy (S3 refuses CopyObject over 5 GiB).</param>
/// <param name="CopyPartSize">The part size of a multipart copy; <c>null</c> for <see cref="S3PartSizes.ForCopy"/>.</param>
internal sealed record S3TransferSettings(
    long PartSize,
    int MaxRetries,
    TimeSpan Timeout,
    int ConditionalConflictAttempts = 5,
    TimeSpan? ConflictBaseDelay = null,
    long CopyObjectMaxBytes = S3Limits.MaxCopyObjectSize,
    long? CopyPartSize = null)
{
    public static S3TransferSettings Default { get; } = new(S3Limits.DefaultPartSize, 5, TimeSpan.FromSeconds(100));

    public TimeSpan FirstConflictDelay => ConflictBaseDelay ?? TimeSpan.FromMilliseconds(100);
}
