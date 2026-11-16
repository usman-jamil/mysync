using System.Diagnostics.CodeAnalysis;

namespace MySync.Domain.Content;

/// <summary>
/// SHA-256 of a file's uncompressed bytes, as 64 lowercase hex characters. It is the object's
/// blob name (CLAUDE.md §6), so the text form is canonical: always lowercase.
/// </summary>
public sealed record ContentHash
{
    public const int ByteLength = 32;
    public const int HexLength = ByteLength * 2;

    private ContentHash(string value) => Value = value;

    public string Value { get; }

    /// <summary>The first two hex characters: the fan-out folder under <c>objects/</c>.</summary>
    public string FanOut => Value[..2];

    public static ContentHash FromBytes(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != ByteLength)
        {
            throw new ArgumentException($"A SHA-256 hash is {ByteLength} bytes.", nameof(sha256));
        }

        return new ContentHash(Convert.ToHexStringLower(sha256));
    }

    /// <summary>Accepts 64 hex characters in either case and stores them lowercase.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ContentHash? hash)
    {
        hash = null;

        if (value is null || value.Length != HexLength || !value.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        // Round-trip through bytes rather than ToLowerInvariant: canonical lowercase hex, no culture.
        hash = new ContentHash(Convert.ToHexStringLower(Convert.FromHexString(value)));
        return true;
    }

    public override string ToString() => Value;
}
