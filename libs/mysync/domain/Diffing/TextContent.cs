using System.Text;

namespace MySync.Domain.Diffing;

public enum TextEncodingKind
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
}

public enum LineEndingStyle
{
    /// <summary>No line break at all (empty, or one line without one).</summary>
    None,
    Lf,
    CrLf,
    Cr,
    Mixed,
}

/// <summary>
/// One side of a diff: binary, or text with its encoding, line endings and lines.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A UTF-16 BOM (LE or BE) means UTF-16; otherwise a NUL in the first 8 KiB, or bytes that
/// aren't valid UTF-8 (after a UTF-8 BOM, if any), mean binary.</item>
/// <item>Lines are split on <c>\n</c> only; a <c>\r</c> stays part of its line, so CRLF and LF lines
/// differ when compared, and the screen hides the <c>\r</c>.</item>
/// <item>A final line without a line break is its own state (<see cref="EndsWithNewline"/>), as in diff/git.</item>
/// </list>
/// </remarks>
public sealed class TextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private TextContent(bool isBinary, TextEncodingKind? encoding, LineEndingStyle lineEndings, bool endsWithNewline, IReadOnlyList<string> lines, int byteLength)
    {
        IsBinary = isBinary;
        Encoding = encoding;
        LineEndings = lineEndings;
        EndsWithNewline = endsWithNewline;
        Lines = lines;
        ByteLength = byteLength;
    }

    public bool IsBinary { get; }

    /// <summary><c>null</c> for binary content.</summary>
    public TextEncodingKind? Encoding { get; }

    public LineEndingStyle LineEndings { get; }

    /// <summary>True when the last line ends with a line break (and for empty content).</summary>
    public bool EndsWithNewline { get; }

    /// <summary>The lines, without their <c>\n</c> (a <c>\r</c> before it is kept). Empty for binary content.</summary>
    public IReadOnlyList<string> Lines { get; }

    public int ByteLength { get; }

    public static TextContent From(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;

        string? text = Decode(span, out TextEncodingKind encoding);
        if (text is null)
        {
            return new TextContent(true, null, LineEndingStyle.None, true, [], bytes.Length);
        }

        List<string> lines = [.. text.Split('\n')];
        bool endsWithNewline = true;
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // the text ended with "\n" (or was empty)
        }
        else
        {
            endsWithNewline = false;
        }

        return new TextContent(false, encoding, StyleOf(text), endsWithNewline, lines, bytes.Length);
    }

    private static string? Decode(ReadOnlySpan<byte> bytes, out TextEncodingKind encoding)
    {
        encoding = TextEncodingKind.Utf8;
        try
        {
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            {
                encoding = TextEncodingKind.Utf16LittleEndian;
                return bytes.Length % 2 == 0 ? StrictUtf16Le.GetString(bytes[2..]) : null;
            }

            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            {
                encoding = TextEncodingKind.Utf16BigEndian;
                return bytes.Length % 2 == 0 ? StrictUtf16Be.GetString(bytes[2..]) : null;
            }

            ReadOnlySpan<byte> body = bytes;
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                encoding = TextEncodingKind.Utf8Bom;
                body = bytes[3..];
            }

            if (body[..Math.Min(body.Length, DiffLimits.BinarySniffBytes)].Contains((byte)0))
            {
                return null;
            }

            return StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static LineEndingStyle StyleOf(string text)
    {
        bool lf = false, crlf = false, cr = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                bool afterCr = i > 0 && text[i - 1] == '\r';
                crlf |= afterCr;
                lf |= !afterCr;
            }
            else if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
            {
                cr = true;
            }
        }

        return (lf, crlf, cr) switch
        {
            (false, false, false) => LineEndingStyle.None,
            (true, false, false) => LineEndingStyle.Lf,
            (false, true, false) => LineEndingStyle.CrLf,
            (false, false, true) => LineEndingStyle.Cr,
            _ => LineEndingStyle.Mixed,
        };
    }
}

/// <summary>How two text sides relate when their raw lines differ.</summary>
public static class TextComparison
{
    /// <summary>The same lines once each side's <c>\r</c> before a line break is ignored: only line endings differ.</summary>
    public static bool OnlyLineEndingsDiffer(TextContent a, TextContent b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.IsBinary || b.IsBinary || a.Lines.Count != b.Lines.Count || a.EndsWithNewline != b.EndsWithNewline)
        {
            return false;
        }

        bool differs = false;
        for (int i = 0; i < a.Lines.Count; i++)
        {
            string left = a.Lines[i];
            string right = b.Lines[i];
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(left.TrimEnd('\r'), right.TrimEnd('\r'), StringComparison.Ordinal))
            {
                return false;
            }

            differs = true;
        }

        return differs || a.LineEndings != b.LineEndings;
    }

    /// <summary>Identical text, but one side has a byte-order mark (or another encoding) and the other doesn't.</summary>
    public static bool OnlyEncodingDiffers(TextContent a, TextContent b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return !a.IsBinary && !b.IsBinary && a.Encoding != b.Encoding
            && a.EndsWithNewline == b.EndsWithNewline && a.Lines.SequenceEqual(b.Lines, StringComparer.Ordinal);
    }
}
