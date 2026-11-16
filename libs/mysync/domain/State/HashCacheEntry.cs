using MySync.Domain.Content;

namespace MySync.Domain.State;

/// <summary>
/// A remembered hash for one local file (CLAUDE.md §7). Reused only if size and mtime both match
/// exactly; the racy-mtime rule for storing entries belongs to the scanner.
/// </summary>
public sealed record HashCacheEntry(long Size, long MtimeTicksUtc, ContentHash Sha256)
{
    public bool Matches(long size, long mtimeTicksUtc) => Size == size && MtimeTicksUtc == mtimeTicksUtc;
}
