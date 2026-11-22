using MySync.Domain.Paths;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Domain.Content;

/// <summary>
/// How an object's bytes are stored (CLAUDE.md §6): as they are, or gzip-compressed. Not part of a
/// file's identity: the object is named by the SHA-256 of the <em>uncompressed</em> content either
/// way, and its form travels in the blob's standard <c>Content-Encoding</c> property.
/// </summary>
public enum ObjectEncoding
{
    /// <summary>The raw file bytes; no <c>Content-Encoding</c>.</summary>
    Identity = 0,

    /// <summary>gzip (RFC 1952); <c>Content-Encoding: gzip</c>.</summary>
    Gzip = 1,
}

public static class ObjectEncodings
{
    /// <summary>The <c>Content-Encoding</c> value for <paramref name="encoding"/>; <c>null</c> for raw bytes.</summary>
    public static string? ToContentEncoding(this ObjectEncoding encoding) => encoding switch
    {
        ObjectEncoding.Gzip => "gzip",
        _ => null,
    };

    /// <summary>
    /// Reads a stored <c>Content-Encoding</c>. Absent, empty or <c>identity</c> is raw; <c>gzip</c>
    /// is gzip. Anything else is <see cref="StorageErrors.UnsupportedEncoding"/>: never guessed.
    /// </summary>
    public static Result<ObjectEncoding> FromContentEncoding(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(trimmed, "identity", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectEncoding.Identity;
        }

        return string.Equals(trimmed, "gzip", StringComparison.OrdinalIgnoreCase)
            ? ObjectEncoding.Gzip
            : Result.Failure<ObjectEncoding>(StorageErrors.UnsupportedEncoding(trimmed));
    }

    internal static string Display(string value) => PathErrors.Display(value.Length > 40 ? value[..40] + "…" : value);
}
