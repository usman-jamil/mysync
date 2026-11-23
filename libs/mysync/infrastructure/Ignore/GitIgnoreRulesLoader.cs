using System.Text;
using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Infrastructure.Ignore;

/// <summary>
/// Reads <c>.mysyncignore</c> and <c>.gitignore</c> from the share root (v1: the root only) and
/// joins them as a union: a path either file ignores is ignored. Both are synced, so they may have
/// been written on another machine: read as UTF-8 (a BOM is fine), lines ending in LF or CRLF, and a
/// file over <see cref="MaxBytes"/> is refused rather than compiled.
/// </summary>
internal sealed class GitIgnoreRulesLoader(ShareRoot root) : IIgnoreRulesLoader
{
    internal const int MaxBytes = 1024 * 1024;

    public async Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken)
    {
        List<IIgnoreMatcher> matchers = [];
        foreach (string name in StateFileNames.IgnoreFiles)
        {
            Result<string?> rules = await ReadAsync(name, cancellationToken);
            if (rules.IsFailure)
            {
                return Result.Failure<IIgnoreMatcher>(rules.Error);
            }

            if (rules.Value is { } text)
            {
                matchers.Add(Compile(text));
            }
        }

        return matchers.Count switch
        {
            0 => Result.Success<IIgnoreMatcher>(GitIgnoreMatcher.BuiltInsOnly),
            1 => Result.Success(matchers[0]),
            _ => Result.Success<IIgnoreMatcher>(new UnionIgnoreMatcher(matchers)),
        };
    }

    public IIgnoreMatcher Compile(string rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new GitIgnoreMatcher(rules.TrimStart('\uFEFF').Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <returns>The file's text, or <c>null</c> when it isn't there.</returns>
    private async Task<Result<string?>> ReadAsync(string name, CancellationToken cancellationToken)
    {
        string path = root.Combine(name);

        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return Result.Success<string?>(null);
            }

            if (file.Length > MaxBytes)
            {
                return Result.Failure<string?>(LocalFileErrors.Unreadable(name, $"it is larger than {MaxBytes / 1024} KB"));
            }

            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<string?>(LocalFileErrors.Unreadable(name, exception.Message));
        }
    }
}
