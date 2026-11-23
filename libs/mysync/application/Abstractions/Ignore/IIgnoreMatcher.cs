using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Application.Abstractions.Ignore;

/// <summary>Built-in excludes plus <c>.mysyncignore</c> (gitignore syntax, CLAUDE.md §7).</summary>
public interface IIgnoreMatcher
{
    bool IsIgnored(SharePath path, bool isDirectory);
}

public interface IIgnoreRulesLoader
{
    /// <summary>
    /// Reads <c>.mysyncignore</c> and <c>.gitignore</c> from the share root, each compiled on its own
    /// and joined as a <see cref="UnionIgnoreMatcher"/>; neither file means built-ins only.
    /// </summary>
    Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The built-in excludes plus rules in <c>.mysyncignore</c> syntax read from somewhere else:
    /// <c>mysync push</c> checks what it adds against the share's own <c>.mysyncignore</c>, read from HEAD.
    /// </summary>
    IIgnoreMatcher Compile(string rules);
}

/// <summary>
/// Ignores a path when <b>any</b> of its matchers does: how <c>.mysyncignore</c> and <c>.gitignore</c>
/// combine. Each file's rules are evaluated on their own, so a <c>!</c> rule in one can't re-include
/// what the other ignores.
/// </summary>
public sealed class UnionIgnoreMatcher : IIgnoreMatcher
{
    private readonly IIgnoreMatcher[] _matchers;

    public UnionIgnoreMatcher(IEnumerable<IIgnoreMatcher> matchers)
    {
        ArgumentNullException.ThrowIfNull(matchers);
        _matchers = [.. matchers];
    }

    public bool IsIgnored(SharePath path, bool isDirectory) => Array.Exists(_matchers, matcher => matcher.IsIgnored(path, isDirectory));
}

public static class IgnoreMatcherExtensions
{
    /// <summary>
    /// Whether the rules match <paramref name="path"/> or any folder above it: a path inside an
    /// ignored directory stays ignored, as in git. For paths that come from elsewhere (HEAD, a
    /// push), which no scan has walked down to.
    /// </summary>
    public static bool IsIgnoredOrInsideIgnored(this IIgnoreMatcher ignore, SharePath path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(ignore);
        ArgumentNullException.ThrowIfNull(path);

        if (ignore.IsIgnored(path, isDirectory))
        {
            return true;
        }

        for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ignore.IsIgnored(ancestor, isDirectory: true))
            {
                return true;
            }
        }

        return false;
    }
}
