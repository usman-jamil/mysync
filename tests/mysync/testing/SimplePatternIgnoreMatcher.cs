using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.State;

namespace MySync.Testing;

/// <summary>
/// Just enough gitignore for handler tests: <c>*.ext</c> matches a name's ending at any depth,
/// <c>name/</c> a directory of that name, <c>a/b</c> (with a slash) that exact path from the root,
/// and a bare <c>name</c> that name at any depth. <c>#</c> comments and blank lines are skipped.
/// Plus mysync's own artifacts. The real matcher is tested against git in Infrastructure.
/// </summary>
public sealed class SimplePatternIgnoreMatcher : IIgnoreMatcher
{
    private readonly List<string> _rules;

    public SimplePatternIgnoreMatcher(string rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];
    }

    public bool IsIgnored(SharePath path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (StateFileNames.AlwaysIgnored.Contains(path.Value, StringComparer.Ordinal))
        {
            return true;
        }

        return _rules.Any(rule => Matches(rule, path, isDirectory));
    }

    private static bool Matches(string rule, SharePath path, bool isDirectory)
    {
        if (rule.StartsWith("*.", StringComparison.Ordinal))
        {
            return path.Name.EndsWith(rule[1..], StringComparison.Ordinal);
        }

        if (rule.EndsWith('/'))
        {
            return isDirectory && path.Name == rule.TrimEnd('/');
        }

        return rule.Contains('/', StringComparison.Ordinal)
            ? path.Value == rule.TrimStart('/')
            : path.Name == rule;
    }
}
