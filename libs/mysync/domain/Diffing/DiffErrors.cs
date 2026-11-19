using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Diffing;

/// <summary>Why <c>mysync diff</c> stopped. Paths go through <see cref="PathErrors.Display"/>.</summary>
public static class DiffErrors
{
    public static Error PathNotFound(string path) => Error.Validation(
        "Diff.PathNotFound", $"'{PathErrors.Display(path)}' is neither in this folder nor in the share.");

    public static Error VersionNotStored(string path, string side) => Error.NotFound(
        "Diff.VersionNotStored", $"The share no longer stores the {side} version of '{PathErrors.Display(path)}'; run mysync sync to refresh the conflict.");

    public static Error VersionMissing(string path, string side) => Error.Validation(
        "Diff.VersionMissing", $"There is no {side} version of '{PathErrors.Display(path)}' to print.");

    public static readonly Error PrintNeedsOnePath = Error.Validation(
        "Diff.PrintNeedsOnePath", "--print writes one version of one file: name exactly one path.");

    public static readonly Error PrintCombined = Error.Validation(
        "Diff.PrintCombined", "--print writes raw bytes only, so it can't be combined with --stat, --base or --json.");
}
