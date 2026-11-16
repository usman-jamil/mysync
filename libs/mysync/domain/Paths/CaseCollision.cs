namespace MySync.Domain.Paths;

/// <summary>
/// Spellings of one path (or directory) that differ only in letter case, sorted ordinally.
/// On a case-insensitive filesystem none of the affected entries is downloaded (CLAUDE.md §8).
/// </summary>
public sealed record CaseCollision(IReadOnlyList<string> Spellings)
{
    /// <summary>True if <paramref name="path"/> is one of the spellings or lies under one of them.</summary>
    public bool Affects(SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string spelling in Spellings)
        {
            if (string.Equals(path.Value, spelling, StringComparison.Ordinal)
                || path.Value.Length > spelling.Length
                    && path.Value[spelling.Length] == SharePath.Separator
                    && path.Value.StartsWith(spelling, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
