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
    /// <summary>Reads <c>.mysyncignore</c> from the share root; a missing file means built-ins only.</summary>
    Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken);
}
