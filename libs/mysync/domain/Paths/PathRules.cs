using System.Buffers;
using System.Collections.Frozen;
using MySync.Domain.Paths.Normalization;
using SharedKernel;

namespace MySync.Domain.Paths;

/// <summary>
/// Rules every manifest path follows (CLAUDE.md §8). Construction-time checks live in
/// <see cref="SharePath"/>; the OS-specific ones (Windows names, case collisions) live here
/// because they are warnings that skip a path, not reasons to reject a manifest.
/// </summary>
public static class PathRules
{
    private static readonly SearchValues<char> WindowsInvalidCharacters = SearchValues.Create("<>:\"|?*");

    private static readonly FrozenSet<string> WindowsReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Unicode version <see cref="ToNfc"/> follows.</summary>
    public static string UnicodeVersion => NfcNormalizer.UnicodeVersion;

    /// <summary>
    /// Manifest paths are NFC. macOS can hand back NFD names, so every path is normalized on
    /// scan and when mapping a manifest entry back to disk.
    /// </summary>
    /// <remarks>
    /// Never <see cref="string.Normalize()"/>: under InvariantGlobalization (release builds) it
    /// returns NFD input unchanged. <see cref="NfcNormalizer"/> does not depend on ICU.
    /// </remarks>
    public static string ToNfc(string path) => NfcNormalizer.Normalize(path);

    /// <summary>
    /// Whether every segment of <paramref name="path"/> can exist on Windows: none of
    /// <c>&lt;&gt;:"|?*</c>, no trailing dot or space, and not a reserved device name
    /// (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1-9</c>, <c>LPT1-9</c>), with or without an
    /// extension (<c>con.txt</c> is reserved too). On Windows such entries are skipped with a warning.
    /// </summary>
    public static Result CheckWindowsCompatible(SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string segment in path.Segments)
        {
            string? reason = WindowsProblem(segment);

            if (reason is not null)
            {
                return Result.Failure(PathErrors.WindowsInvalidName(path.Value, reason));
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Paths that a case-insensitive filesystem (Windows, default macOS) would treat as the same:
    /// two files (<c>Foo.txt</c> / <c>foo.txt</c>) or two directories (<c>Docs/a</c> / <c>docs/b</c>).
    /// Each collision is reported once, at the shallowest level where the spellings diverge.
    /// </summary>
    /// <remarks>
    /// Uses ordinal case-insensitive comparison (simple case mapping, no ICU), close to NTFS's and
    /// APFS's behaviour. <c>ß</c> and <c>SS</c> are not a collision, matching NTFS.
    /// </remarks>
    public static IReadOnlyList<CaseCollision> FindCaseCollisions(IEnumerable<SharePath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Every path and every ancestor prefix, grouped case-insensitively, keeping each spelling.
        Dictionary<string, SortedSet<string>> spellings = new(StringComparer.OrdinalIgnoreCase);

        foreach (SharePath path in paths)
        {
            for (SharePath? prefix = path; prefix is not null; prefix = prefix.Parent)
            {
                if (!spellings.TryGetValue(prefix.Value, out SortedSet<string>? set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    spellings[prefix.Value] = set;
                }

                set.Add(prefix.Value);
            }
        }

        HashSet<string> colliding = new(StringComparer.OrdinalIgnoreCase);
        List<CaseCollision> collisions = [];

        // Shallowest first, so a collision below an already-colliding directory is not repeated.
        foreach ((string key, SortedSet<string> set) in spellings
                     .Where(group => group.Value.Count > 1)
                     .OrderBy(group => group.Key.Count(c => c == SharePath.Separator))
                     .ThenBy(group => group.Value.Min, StringComparer.Ordinal))
        {
            colliding.Add(key);

            if (!HasCollidingAncestor(key, colliding))
            {
                collisions.Add(new CaseCollision([.. set]));
            }
        }

        return collisions;
    }

    private static bool HasCollidingAncestor(string path, HashSet<string> colliding)
    {
        for (int index = path.LastIndexOf(SharePath.Separator); index > 0; index = path.LastIndexOf(SharePath.Separator, index - 1))
        {
            if (colliding.Contains(path[..index]))
            {
                return true;
            }
        }

        return false;
    }

    private static string? WindowsProblem(string segment)
    {
        int invalid = segment.AsSpan().IndexOfAny(WindowsInvalidCharacters);
        if (invalid >= 0)
        {
            return $"'{segment[invalid]}' is not allowed in a Windows file name";
        }

        if (segment[^1] is '.' or ' ')
        {
            return "Windows file names can't end with a dot or a space";
        }

        int dot = segment.IndexOf('.', StringComparison.Ordinal);
        string baseName = dot < 0 ? segment : segment[..dot];

        return WindowsReservedNames.Contains(baseName) ? $"'{baseName}' is a reserved device name" : null;
    }
}
