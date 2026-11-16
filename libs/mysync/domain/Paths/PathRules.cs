using MySync.Domain.Paths.Normalization;

namespace MySync.Domain.Paths;

/// <summary>
/// Rules every manifest path follows (CLAUDE.md §8). Only Unicode normalization lives here so
/// far; validation and <c>SharePath</c> follow.
/// </summary>
public static class PathRules
{
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
}
