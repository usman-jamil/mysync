using System.Text;

namespace MySync.Cli.Rendering;

/// <summary>
/// Makes untrusted text (a line of a synced file) safe to show on a terminal: control characters
/// become visible symbols (ESC → <c>␛</c>), so a file can't send escape sequences through
/// <c>mysync diff</c>. A trailing <c>\r</c> (CRLF line) is dropped. Markup escaping is separate.
/// Only for the screen: plain output to a pipe stays byte-exact.
/// </summary>
internal static class TerminalText
{
    public static string Visible(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string text = line.EndsWith('\r') ? line[..^1] : line;
        StringBuilder? visible = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string? replacement = c switch
            {
                '\t' => null,
                < ' ' => ((char)(0x2400 + c)).ToString(),
                '\u007f' => "␡",
                >= '\u0080' and <= '\u009f' => $"\\u{(int)c:X4}",
                _ => null,
            };
            if (replacement is null)
            {
                visible?.Append(c);
                continue;
            }

            visible ??= new StringBuilder(text, 0, i, text.Length + 8);
            visible.Append(replacement);
        }

        return visible?.ToString() ?? text;
    }
}
