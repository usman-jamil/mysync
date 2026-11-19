namespace MySync.Domain.Diffing;

/// <summary>What <c>mysync diff</c> diffs in memory, and the context it allows. One place for the limits.</summary>
public static class DiffLimits
{
    /// <summary>Each side is read up to this many bytes; beyond it the path is "too large for an inline diff".</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Each side's line count limit, for the same reason.</summary>
    public const int MaxLines = 200_000;

    public const int DefaultContext = 3;
    public const int MinContext = 0;
    public const int MaxContext = 1000;

    /// <summary>How far into a file binary detection looks for a NUL byte.</summary>
    public const int BinarySniffBytes = 8 * 1024;

    /// <summary>
    /// Work budget for one Myers search (diagonal steps). Past it, that part of the diff is replaced
    /// as a block: still a correct edit script, just not a minimal one. Typical files never get near it.
    /// </summary>
    public const long MaxSearchSteps = 50_000_000;
}
