using SharedKernel;

namespace MySync.Domain.Paths;

/// <summary>
/// Failures reading or writing one path in the local folder. The sync skips that path (it keeps
/// its base entry and is retried next run) rather than failing the whole run (CLAUDE.md §12).
/// </summary>
public static class LocalFileErrors
{
    public static Error Unreadable(string path, string detail) => Error.Problem(
        "Local.Unreadable", $"'{PathErrors.Display(path)}' could not be read: {detail}");

    public static Error WriteFailed(string path, string detail) => Error.Problem(
        "Local.WriteFailed", $"'{PathErrors.Display(path)}' could not be written: {detail}");
}
