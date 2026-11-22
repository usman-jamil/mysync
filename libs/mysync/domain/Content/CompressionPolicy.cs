using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Content;

/// <summary>The outcome of compressing a file's first <see cref="CompressionPolicy.SampleBytes"/> (or all of it, if smaller).</summary>
public readonly record struct CompressionSample(int SampleLength, long CompressedLength);

/// <summary>
/// Whether one file is stored compressed (CLAUDE.md §6). Pure: the caller compresses the sample.
/// Off for the share → never. Under <see cref="MinimumSize"/> bytes → never (gzip's header would
/// outweigh it). A type that is already compressed (<see cref="PrecompressedExtensions"/>) → never.
/// Otherwise a sample decides: if it doesn't shrink to at most 90%, the file is stored raw.
/// </summary>
public static class CompressionPolicy
{
    public const int MinimumSize = 512;

    public const int SampleBytes = 1024 * 1024;

    /// <summary>A sample compressed to more than <see cref="MaxRatioNumerator"/>/<see cref="MaxRatioDenominator"/> of its length isn't worth it.</summary>
    public const int MaxRatioNumerator = 9;

    public const int MaxRatioDenominator = 10;

    /// <summary>Extensions (lowercase, no dot) whose content is already compressed. The one list.</summary>
    public static IReadOnlySet<string> PrecompressedExtensions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // archives
        "zip", "gz", "tgz", "bz2", "xz", "zst", "7z", "rar", "cab",
        // images
        "jpg", "jpeg", "png", "gif", "webp", "heic", "avif",
        // audio and video
        "mp3", "aac", "ogg", "flac", "mp4", "mkv", "mov", "avi", "webm",
        // documents that are zip archives inside
        "docx", "xlsx", "pptx", "odt", "ods", "jar", "nupkg", "apk",
        // other
        "pdf", "woff", "woff2",
    };

    /// <summary>True when a sample has to be compressed to decide: compression is on, and nothing rules the file out.</summary>
    public static bool NeedsSample(CompressionSetting setting, SharePath path, long size)
    {
        ArgumentNullException.ThrowIfNull(path);
        return setting == CompressionSetting.Gzip && size >= MinimumSize && !IsPrecompressed(path);
    }

    /// <summary>The form to store the file in, once its sample has been compressed.</summary>
    public static ObjectEncoding Decide(CompressionSetting setting, SharePath path, long size, CompressionSample sample)
    {
        if (!NeedsSample(setting, path, size) || sample.SampleLength <= 0)
        {
            return ObjectEncoding.Identity;
        }

        return sample.CompressedLength * MaxRatioDenominator > (long)sample.SampleLength * MaxRatioNumerator
            ? ObjectEncoding.Identity
            : ObjectEncoding.Gzip;
    }

    /// <summary>By the extension of the last segment, compared case-insensitively.</summary>
    public static bool IsPrecompressed(SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = path.Name;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return false;
        }

        string extension = name[(dot + 1)..];
        return PrecompressedExtensions.Contains(AsciiLower(extension));
    }

    private static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
}
