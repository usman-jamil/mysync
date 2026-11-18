namespace MySync.Storage.Azure;

/// <summary>
/// Azure's server-side copy limits, in one place. Checked 2026-11-18 against the REST API docs
/// (service version 2020-04-08 and later):
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/rest/api/storageservices/put-blob-from-url">Put Blob From URL</see>:
/// a source of at most 5000 MiB, in one request.</item>
/// <item><see href="https://learn.microsoft.com/rest/api/storageservices/put-block-from-url">Put Block From URL</see>:
/// at most 4000 MiB per block, 50,000 blocks per blob.</item>
/// <item>Copy Blob From URL (synchronous) stops at 256 MiB, so mysync doesn't use it.</item>
/// </list>
/// Azurite 3.37 has no Put Blob From URL (501): the block path covers it there, and for larger files.
/// </summary>
internal static class AzureCopyLimits
{
    private const long MiB = 1024 * 1024;

    public const long PutBlobFromUrlMaxBytes = 5000 * MiB;

    public const long PutBlockFromUrlMaxBytes = 4000 * MiB;

    public const int MaxBlocks = 50_000;

    /// <summary>The block size mysync stages from a URL: up to ~12 TiB per file, comfortably inside the limits above.</summary>
    public const long BlockFromUrlSize = 256 * MiB;
}
