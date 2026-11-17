using System.Text;
using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Infrastructure.Ignore;

/// <summary>
/// Reads <c>.mysyncignore</c> from the share root (v1: the root only). It is synced, so it may
/// have been written on another machine: it is read as UTF-8 (a BOM is fine), lines may end in
/// LF or CRLF, and a file over <see cref="MaxBytes"/> is refused rather than compiled.
/// </summary>
internal sealed class GitIgnoreRulesLoader(ShareRoot root) : IIgnoreRulesLoader
{
    internal const int MaxBytes = 1024 * 1024;

    public async Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken)
    {
        string path = root.Combine(StateFileNames.IgnoreFile);

        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return Result.Success<IIgnoreMatcher>(GitIgnoreMatcher.BuiltInsOnly);
            }

            if (file.Length > MaxBytes)
            {
                return Result.Failure<IIgnoreMatcher>(LocalFileErrors.Unreadable(StateFileNames.IgnoreFile, $"it is larger than {MaxBytes / 1024} KB"));
            }

            string text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            return Result.Success<IIgnoreMatcher>(new GitIgnoreMatcher(text.Split('\n').Select(line => line.TrimEnd('\r'))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<IIgnoreMatcher>(LocalFileErrors.Unreadable(StateFileNames.IgnoreFile, exception.Message));
        }
    }
}
