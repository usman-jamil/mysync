using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

/// <summary>Why <c>mysync push</c> stopped before uploading anything (CLAUDE.md §10b).</summary>
public static class PushErrors
{
    /// <summary>Two arguments would land at the same name in the target folder. Exit 2.</summary>
    public static Error DuplicateSource(string name, string first, string second) => Error.Validation(
        "Push.DuplicateSource",
        $"'{PathErrors.Display(first)}' and '{PathErrors.Display(second)}' would both be pushed as '{PathErrors.Display(name)}'. Push them separately, or rename one.");

    /// <summary>A path argument that doesn't exist. Exit 2.</summary>
    public static Error SourceNotFound(string path) => Error.Validation(
        "Push.SourceNotFound",
        $"'{PathErrors.Display(path)}' doesn't exist. Nothing was pushed.");

    /// <summary>A source folder (or one of its parents) already syncs with the target share. Exit 4; <c>--force</c> doesn't override it.</summary>
    public static Error SourceIsTheShare(string folder, string share) => Error.Conflict(
        "Push.SourceIsTheShare",
        $"'{PathErrors.Display(folder)}' already syncs with {share}; use mysync sync. Pushing it would add a second copy that the next sync downloads back into the folder.");

    /// <summary>The target folder (or a source's name under it) can't be a share path every machine can download. Exit 2.</summary>
    public static Error InvalidTarget(string reason) => Error.Validation(
        "Push.InvalidTarget",
        $"Can't push there: {reason}");

    /// <summary>The share has a file where the host folder would go. Exit 4.</summary>
    public static Error TargetIsAFile(string host) => Error.Conflict(
        "Push.TargetIsAFile",
        $"The share has a file named '{PathErrors.Display(host)}' at its root, so this host's folder can't go there. Pass --host <another name>.");

    /// <summary><c>--create</c> with a share ID: a new share needs a name. Exit 2.</summary>
    public static Error CreateNeedsName() => Error.Validation(
        "Push.CreateNeedsName",
        "--create needs a share name, not a share ID: mysync push <name> --create");
}
