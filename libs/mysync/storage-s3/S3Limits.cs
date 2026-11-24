namespace MySync.Storage.S3;

/// <summary>
/// S3's object and multipart limits, in one place (checked 2026-11-24 against the S3 User Guide,
/// "Amazon S3 multipart upload limits"): parts of 5 MiB to 5 GiB (the last may be smaller), at most
/// 10,000 parts, objects up to 5 TiB, a single PutObject or CopyObject up to 5 GiB.
/// </summary>
internal static class S3Limits
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024 * MiB;
    public const long TiB = 1024 * GiB;

    public const long MinPartSize = 5 * MiB;
    public const long MaxPartSize = 5 * GiB;
    public const int MaxParts = 10_000;
    public const long MaxObjectSize = 5 * TiB;
    public const long MaxSinglePutSize = 5 * GiB;
    public const long MaxCopyObjectSize = 5 * GiB;

    /// <summary>The part size mysync starts with (and never goes below), unless a test lowers it.</summary>
    public const long DefaultPartSize = 16 * MiB;

    /// <summary>
    /// The largest part mysync buffers: a part is held in memory once (a .NET array stops just short
    /// of 2 GiB), well inside S3's 5 GiB.
    /// </summary>
    public const long MaxBufferedPartSize = 1 * GiB;

    /// <summary>Parts uploaded at one size before the next one doubles, when the length isn't known.</summary>
    public const int PartsPerSizeStep = 1_000;
}

/// <summary>
/// How big each part of a multipart upload is (CLAUDE.md §11, "S3 differences"). Pure arithmetic,
/// unit-tested at the limits.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Known length</b> (raw bytes): one size for every part, <c>max(16 MiB, ceil(length / 10,000))</c>,
/// so any object S3 accepts fits in 10,000 parts.</item>
/// <item><b>Unknown length</b> (a gzip stream): start at that size for the uncompressed length
/// (an upper bound in practice: mysync only compresses what shrinks), and double every 1,000 parts
/// up to 1 GiB, so even a stream far longer than expected fits. Starting at 16 MiB that reaches
/// <see cref="MaxUnknownLength"/> (about 4.87 TiB) by part 10,000; past that the upload fails
/// with <c>Storage.TooLarge</c> rather than at part 10,001.</item>
/// </list>
/// </remarks>
internal static class S3PartSizes
{
    /// <summary>The one part size for an upload of exactly <paramref name="length"/> bytes.</summary>
    public static long ForKnownLength(long length, long minimum = S3Limits.DefaultPartSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long needed = (length + S3Limits.MaxParts - 1) / S3Limits.MaxParts;
        return Math.Max(minimum, needed);
    }

    /// <summary>
    /// The size of part <paramref name="partNumber"/> (1-based) of an upload of unknown length whose
    /// first part is <paramref name="first"/>: doubled every <see cref="S3Limits.PartsPerSizeStep"/> parts,
    /// never above <see cref="S3Limits.MaxBufferedPartSize"/>.
    /// </summary>
    public static long ForPart(int partNumber, long first)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(first, 1);
        int doublings = Math.Min((partNumber - 1) / S3Limits.PartsPerSizeStep, 30);
        long size = first;
        for (int step = 0; step < doublings && size < S3Limits.MaxBufferedPartSize; step++)
        {
            size *= 2;
        }

        return Math.Min(size, Math.Max(first, S3Limits.MaxBufferedPartSize));
    }

    /// <summary>The most an upload of unknown length starting at <paramref name="first"/> can hold in 10,000 parts (and S3's 5 TiB).</summary>
    public static long MaxUnknownLength(long first = S3Limits.DefaultPartSize)
    {
        long total = 0;
        for (int part = 1; part <= S3Limits.MaxParts; part++)
        {
            total += ForPart(part, first);
        }

        return Math.Min(total, S3Limits.MaxObjectSize);
    }

    /// <summary>The part size for a multipart copy of <paramref name="length"/> bytes (UploadPartCopy): 512 MiB, or more so it fits in 10,000 parts.</summary>
    public static long ForCopy(long length) => ForKnownLength(length, 512 * S3Limits.MiB);
}
