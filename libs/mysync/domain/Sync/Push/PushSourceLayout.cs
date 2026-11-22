using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

public enum PushSourceKind
{
    File = 0,
    Folder = 1,
}

/// <summary>One thing to push, as the command line named it.</summary>
/// <param name="Argument">As typed (for messages).</param>
/// <param name="Name">Its own name on disk (the last segment of its full path); empty for a filesystem root.</param>
public sealed record PushSource(string Argument, PushSourceKind Kind, string Name);

/// <summary>Where a source lands, relative to the target folder.</summary>
/// <param name="Root">Its name under the target folder, or <c>null</c> for the current folder, whose <b>contents</b> land directly in the target folder.</param>
public sealed record PushSourceRoot(PushSource Source, SharePath? Root);

/// <summary>
/// Maps <c>mysync push</c>'s path arguments to where they land (CLAUDE.md §10b):
/// <list type="bullet">
/// <item>no arguments: the current folder's contents directly under the target folder;</item>
/// <item>a file: <c>&lt;target&gt;/&lt;its name&gt;</c>;</item>
/// <item>a folder: <c>&lt;target&gt;/&lt;its name&gt;/…</c> (its own name is kept).</item>
/// </list>
/// Two arguments that would land at the same name are <see cref="PushErrors.DuplicateSource"/>.
/// </summary>
public static class PushSourceLayout
{
    /// <summary>No path arguments: the current folder, contents at the target root.</summary>
    public static PushSourceRoot CurrentFolder(string fullPath) =>
        new(new PushSource(fullPath, PushSourceKind.Folder, string.Empty), null);

    /// <summary>The path arguments, each under its own name; checked for clashes before anything is read.</summary>
    public static Result<IReadOnlyList<PushSourceRoot>> Map(IReadOnlyList<PushSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        Dictionary<string, PushSource> taken = new(StringComparer.Ordinal);
        List<PushSourceRoot> roots = [];

        foreach (PushSource source in sources)
        {
            if (source.Name.Length == 0)
            {
                return Result.Failure<IReadOnlyList<PushSourceRoot>>(PushErrors.InvalidTarget(
                    $"'{PathErrors.Display(source.Argument)}' is a filesystem root, which has no name to push it under. Push the folders inside it instead."));
            }

            Result<SharePath> name = SharePath.Create(source.Name);
            if (name.IsFailure || name.Value.Segments.Count != 1)
            {
                return Result.Failure<IReadOnlyList<PushSourceRoot>>(PushErrors.InvalidTarget(
                    $"'{PathErrors.Display(source.Argument)}' has a name no share path can hold ({(name.IsFailure ? name.Error.Description : "it contains '/'")})."));
            }

            if (taken.TryGetValue(name.Value.Value, out PushSource? first))
            {
                return Result.Failure<IReadOnlyList<PushSourceRoot>>(PushErrors.DuplicateSource(name.Value.Value, first.Argument, source.Argument));
            }

            taken[name.Value.Value] = source;
            roots.Add(new PushSourceRoot(source, name.Value));
        }

        return roots;
    }
}
