namespace MySync.Application.Sync;

/// <summary>
/// Tuning for transfers and commits. The CLI binds <see cref="MaxParallelism"/> from
/// <c>MYSYNC_PARALLELISM</c>; a command's <c>--parallel</c> overrides it per run.
/// </summary>
public sealed record SyncOptions
{
    public const int MinParallelism = 1;
    public const int MaxAllowedParallelism = 64;

    /// <summary>Concurrent uploads or downloads. Default <c>min(ProcessorCount * 2, 16)</c>.</summary>
    public int MaxParallelism { get; init; } = Math.Min(System.Environment.ProcessorCount * 2, 16);

    /// <summary>Commit attempts before <c>Sync.CommitRetriesExhausted</c> (CLAUDE.md §6).</summary>
    public int MaxCommitAttempts { get; init; } = 5;

    /// <summary>First back-off after a lost commit race; doubles each attempt, ±50% jitter.</summary>
    public TimeSpan CommitRetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// gzip level for uploads to a share with compression on (CLAUDE.md §6). <c>Fastest</c> by
    /// default; the CLI binds <c>MYSYNC_COMPRESSION_LEVEL=fastest|optimal</c>.
    /// </summary>
    public System.IO.Compression.CompressionLevel CompressionLevel { get; init; } = System.IO.Compression.CompressionLevel.Fastest;
}
