using System.Text;

namespace MySync.Domain.Publishing;

public enum Disposition
{
    Inline,
    Attachment,
}

/// <summary>
/// Whether the browser shows a published file or downloads it. Everything downloads except a
/// short list of passive types. HTML, SVG, XML, XHTML and JavaScript always download: shown
/// inline they would run script on the publishing origin, next to every other publication.
/// The same answer goes into the blob's Content-Disposition and into <c>site-data.js</c>.
/// </summary>
public static class DispositionPolicy
{
    private static readonly HashSet<string> InlineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".json", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp",
    };

    public static Disposition For(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return InlineExtensions.Contains(ContentTypeMap.Extension(fileName)) ? Disposition.Inline : Disposition.Attachment;
    }

    /// <summary><c>attachment; filename*=UTF-8''&lt;name&gt;</c> (RFC 6266 / 8187), the name percent-encoded.</summary>
    public static string ContentDisposition(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string kind = For(fileName) == Disposition.Inline ? "inline" : "attachment";
        return $"{kind}; filename*=UTF-8''{EncodeRfc8187(fileName[(fileName.LastIndexOf('/') + 1)..])}";
    }

    // attr-char = ALPHA / DIGIT / "!" / "#" / "$" / "&" / "+" / "-" / "." / "^" / "_" / "`" / "|" / "~"
    internal static string EncodeRfc8187(string value)
    {
        StringBuilder encoded = new();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "!#$&+-.^_`|~".Contains(c, StringComparison.Ordinal))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
