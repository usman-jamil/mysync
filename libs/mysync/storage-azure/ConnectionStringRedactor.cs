using System.Text.RegularExpressions;

namespace MySync.Storage.Azure;

/// <summary>
/// Masks secrets in any text that might carry a connection string, SAS token or key (CLAUDE.md §5):
/// <c>AccountKey=***</c>, <c>SharedAccessSignature=***</c>, and the <c>sig=</c> of a SAS URL.
/// Applied to everything from the Azure SDK before it is logged; error descriptions never include
/// SDK text at all.
/// </summary>
public static partial class ConnectionStringRedactor
{
    public const string Mask = "***";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = KeyValueSecret().Replace(text, match => $"{match.Groups["name"].Value}={Mask}");
        return SasSignature().Replace(redacted, match => $"{match.Groups["name"].Value}={Mask}");
    }

    // AccountKey=...; SharedAccessSignature=...; up to the next ';', quote or whitespace.
    [GeneratedRegex(@"(?<name>AccountKey|SharedAccessSignature)\s*=\s*[^;""'\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValueSecret();

    // ...?sv=...&sig=abc%2B...: the signature is what grants access.
    [GeneratedRegex(@"(?<name>[?&]sig)=[^&\s""']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SasSignature();
}
