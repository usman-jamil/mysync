using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.State;

namespace MySync.Testing;

/// <summary>
/// Ignores mysync's own artifacts plus an explicit list of paths. Not gitignore: handler tests
/// only need "this path is ignored"; real pattern matching is tested in Infrastructure.
/// </summary>
public sealed class PathListIgnoreMatcher(params string[] ignoredPaths) : IIgnoreMatcher
{
    private readonly HashSet<string> _ignored = new(StateFileNames.AlwaysIgnored.Concat(ignoredPaths), StringComparer.Ordinal);

    public static PathListIgnoreMatcher BuiltInsOnly { get; } = new();

    public bool IsIgnored(SharePath path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _ignored.Contains(path.Value);
    }
}
