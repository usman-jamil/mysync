using System.Collections.Concurrent;
using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.State;

namespace MySync.Infrastructure.Ignore;

/// <summary>
/// Built-in excludes plus the rules of <c>.mysyncignore</c> (CLAUDE.md §7).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Built-ins match a name at any depth and can't be re-included with <c>!</c>: mysync's own
/// artifacts (a nested share's state must never sync) and OS clutter.</item>
/// <item>Otherwise the last matching rule wins, as in git.</item>
/// <item>A path inside an ignored directory is ignored, whatever later rules say: git can't
/// re-include a file if a parent directory is excluded, and neither can we.</item>
/// </list>
/// Thread-safe; results for directories are cached, since every path asks about its ancestors.
/// </remarks>
internal sealed class GitIgnoreMatcher : IIgnoreMatcher
{
    private static readonly HashSet<string> BuiltInNames = new(StateFileNames.AlwaysIgnored, StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<IgnoreRule> _rules;
    private readonly ConcurrentDictionary<string, bool> _directories = new(StringComparer.Ordinal);

    public GitIgnoreMatcher(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _rules = [.. lines.Select(IgnoreRule.Parse).OfType<IgnoreRule>()];
    }

    public static GitIgnoreMatcher BuiltInsOnly { get; } = new([]);

    public int RuleCount => _rules.Count;

    public bool IsIgnored(SharePath path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);

        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (IsDirectoryIgnored(ancestor))
            {
                return true;
            }
        }

        return isDirectory ? IsDirectoryIgnored(path) : MatchesItself(path.Value, path.Name, isDirectory: false);
    }

    // Ancestors are checked by the caller loop, so this looks at the directory's own name only.
    private bool IsDirectoryIgnored(SharePath directory) =>
        _directories.GetOrAdd(directory.Value, static (value, state) => state.Matcher.MatchesItself(value, state.Name, isDirectory: true), (Matcher: this, directory.Name));

    private bool MatchesItself(string path, string name, bool isDirectory)
    {
        if (BuiltInNames.Contains(name) || StateFileNames.IsOsClutter(name))
        {
            return true;
        }

        bool ignored = false;
        foreach (IgnoreRule rule in _rules)
        {
            if (rule.Negated == ignored && rule.Matches(path, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }
}
