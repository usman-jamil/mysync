using System.Text;
using System.Text.RegularExpressions;
using MySync.Domain.Paths;

namespace MySync.Infrastructure.Ignore;

/// <summary>
/// One line of <c>.mysyncignore</c>, in gitignore syntax (https://git-scm.com/docs/gitignore),
/// compiled to a regular expression over a whole <see cref="SharePath"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Blank lines and lines starting with <c>#</c> are skipped; <c>\#</c> and <c>\!</c> are literals.</item>
/// <item>Trailing spaces are dropped unless escaped (<c>x\ </c>).</item>
/// <item><c>!</c> negates; a trailing <c>/</c> matches directories only.</item>
/// <item>A pattern with a <c>/</c> at the start or in the middle is anchored to the share root;
/// otherwise it matches a name at any depth.</item>
/// <item><c>*</c>, <c>?</c> and <c>[...]</c> (with <c>!</c> or <c>^</c> negation, ranges and POSIX
/// classes) never match <c>/</c>. A malformed bracket expression makes the line match nothing, as in git. <c>**</c> as a whole segment matches any number of directories:
/// <c>**/x</c>, <c>a/**/b</c>, <c>a/**</c>. Elsewhere it is an ordinary <c>*</c>.</item>
/// <item>Matching is case-sensitive on every OS, so a team's rules mean the same everywhere.</item>
/// <item>The pattern is normalized to NFC, like the paths it is matched against.</item>
/// </list>
/// The regex uses <see cref="RegexOptions.NonBacktracking"/>: <c>.mysyncignore</c> is synced, so a
/// pattern written on another machine must not be able to make matching take exponential time.
/// </remarks>
internal sealed class IgnoreRule
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private static readonly Dictionary<string, string> PosixClasses = new(StringComparer.Ordinal)
    {
        ["alnum"] = "a-zA-Z0-9",
        ["alpha"] = "a-zA-Z",
        ["blank"] = @" \t",
        ["digit"] = "0-9",
        ["lower"] = "a-z",
        ["upper"] = "A-Z",
        ["space"] = @" \t\n\r\f\v",
        ["xdigit"] = "0-9A-Fa-f",
        ["punct"] = @"!-/:-@\[-`{-~",
    };

    private readonly Regex _regex;
    private readonly Regex? _directoryItself;

    private IgnoreRule(string pattern, bool negated, bool directoryOnly, Regex regex, Regex? directoryItself)
    {
        Pattern = pattern;
        Negated = negated;
        DirectoryOnly = directoryOnly;
        _regex = regex;
        _directoryItself = directoryItself;
    }

    /// <summary>The line as written, for diagnostics.</summary>
    public string Pattern { get; }

    public bool Negated { get; }

    public bool DirectoryOnly { get; }

    public bool Matches(string path, bool isDirectory) =>
        (isDirectory || !DirectoryOnly)
        && (_regex.IsMatch(path) || isDirectory && _directoryItself?.IsMatch(path) == true);

    /// <summary>The rule for one line, or <c>null</c> for a blank line, a comment or a pattern that matches nothing.</summary>
    public static IgnoreRule? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.StartsWith('#'))
        {
            return null;
        }

        string text = TrimUnescapedTrailingSpaces(line);
        bool negated = text.StartsWith('!');
        if (negated)
        {
            text = text[1..];
        }

        bool directoryOnly = text.EndsWith('/') && !EndsWithEscape(text[..^1]);
        if (directoryOnly)
        {
            text = text[..^1];
        }

        if (text.Length == 0)
        {
            return null;
        }

        text = PathRules.ToNfc(text);
        bool anchored = text.Contains('/', StringComparison.Ordinal);
        if (text.StartsWith('/'))
        {
            text = text[1..];
        }

        string[] segments = text.Split('/');
        if (segments.Any(segment => segment.Length == 0))
        {
            return null; // "a//b": git never matches it either
        }

        Regex? regex = Compile(segments, anchored);
        if (regex is null)
        {
            return null;
        }

        // Like git, "a/**" also matches the directory a itself (but not a file named a).
        Regex? directoryItself = segments.Length > 1 && segments[^1] == "**" ? Compile(segments[..^1], anchored) : null;

        return new IgnoreRule(line, negated, directoryOnly, regex, directoryItself);
    }

    // Null when the pattern is malformed (an unclosed '[', an unknown [:class:]): git matches nothing then.
    private static Regex? Compile(string[] segments, bool anchored)
    {
        StringBuilder regex = new("^");
        if (!anchored)
        {
            regex.Append("(?:.*/)?");
        }

        if (!AppendSegments(regex, segments))
        {
            return null;
        }

        regex.Append('$');
        return new Regex(regex.ToString(), Options);
    }

    private static bool AppendSegments(StringBuilder regex, string[] segments)
    {
        for (int i = 0; i < segments.Length; i++)
        {
            bool first = i == 0;
            bool last = i == segments.Length - 1;

            if (segments[i] == "**")
            {
                regex.Append((first, last) switch
                {
                    (true, true) => ".*",          // "**": everything
                    (true, false) => "(?:.*/)?",   // "**/x": x at any depth
                    (false, true) => "/.+",        // "a/**": everything inside a, not a itself
                    _ => "(?:/.+)?",               // "a/**/b": zero or more directories between
                });
                continue;
            }

            bool afterLeadingDoubleStar = i == 1 && segments[0] == "**";
            if (!first && !afterLeadingDoubleStar)
            {
                regex.Append('/');
            }

            if (!AppendGlob(regex, segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AppendGlob(StringBuilder regex, string glob)
    {
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            switch (c)
            {
                case '\\' when i + 1 < glob.Length:
                    i++;
                    regex.Append(Regex.Escape(glob[i].ToString()));
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                    if (!TryParseClass(glob, i, out string? characterClass, out int end))
                    {
                        return false;
                    }

                    regex.Append(characterClass);
                    i = end;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }

            i++;
        }

        return true;
    }

    // A bracket expression starting at glob[start] == '[', as a regex class that never matches '/'.
    private static bool TryParseClass(string glob, int start, out string? characterClass, out int end)
    {
        characterClass = null;
        end = start;
        int i = start + 1;
        bool negated = i < glob.Length && glob[i] is '!' or '^';
        if (negated)
        {
            i++;
        }

        StringBuilder members = new();
        bool firstMember = true;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == ']' && !firstMember)
            {
                characterClass = negated ? $"[^/{members}]" : $"[{members}]";
                end = i;
                return members.Length > 0;
            }

            firstMember = false;
            if (c == '[' && i + 1 < glob.Length && glob[i + 1] == ':')
            {
                int close = glob.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close < 0 || !PosixClasses.TryGetValue(glob[(i + 2)..close], out string? posix))
                {
                    return false; // unknown class
                }

                members.Append(posix);
                i = close + 2;
                continue;
            }

            if (c == '\\' && i + 1 < glob.Length)
            {
                c = glob[++i];
            }

            if (c == '-' && members.Length > 0 && i + 1 < glob.Length && glob[i + 1] != ']')
            {
                members.Append('-');
            }
            else
            {
                members.Append(EscapeInClass(c));
            }

            i++;
        }

        return false; // no closing ']'
    }

    private static string EscapeInClass(char c) => c is '\\' or ']' or '[' or '^' or '-' ? $"\\{c}" : c.ToString();

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !EndsWithEscape(line[..(end - 1)]))
        {
            end--;
        }

        return line[..end];
    }

    // Whether the text ends in an odd number of backslashes (so the next character is escaped).
    private static bool EndsWithEscape(string text)
    {
        int count = 0;
        for (int i = text.Length - 1; i >= 0 && text[i] == '\\'; i--)
        {
            count++;
        }

        return count % 2 == 1;
    }
}
