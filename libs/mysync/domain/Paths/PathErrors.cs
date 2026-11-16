using System.Globalization;
using System.Text;
using SharedKernel;

namespace MySync.Domain.Paths;

/// <summary>
/// Path validation failures. Paths in these messages go through <see cref="Display"/>: a path from
/// a crafted manifest could otherwise carry terminal escape sequences into the user's console.
/// </summary>
public static class PathErrors
{
    public static Error Empty() => Error.Validation(
        "Paths.Empty", "A path can't be empty.");

    public static Error ControlCharacter(string path) => Error.Validation(
        "Paths.ControlCharacter", $"'{Display(path)}' contains a control character.");

    public static Error Backslash(string path) => Error.Validation(
        "Paths.Backslash", $"'{Display(path)}' contains a backslash, which Windows treats as a separator.");

    public static Error UnpairedSurrogate(string path) => Error.Validation(
        "Paths.UnpairedSurrogate", $"'{Display(path)}' is not valid Unicode (unpaired surrogate).");

    public static Error Absolute(string path) => Error.Validation(
        "Paths.Absolute", $"'{Display(path)}' is absolute; share paths are relative to the share root.");

    public static Error DriveLetter(string path) => Error.Validation(
        "Paths.DriveLetter", $"'{Display(path)}' starts with a drive letter.");

    public static Error EmptySegment(string path) => Error.Validation(
        "Paths.EmptySegment", $"'{Display(path)}' has an empty segment (leading, trailing or double '/').");

    public static Error RelativeSegment(string path) => Error.Validation(
        "Paths.RelativeSegment", $"'{Display(path)}' contains a '.' or '..' segment.");

    public static Error WindowsInvalidName(string path, string reason) => Error.Validation(
        "Paths.WindowsInvalidName", $"'{Display(path)}' can't be created on Windows: {reason}.");

    /// <summary>
    /// The path with control characters and unpaired surrogates written as <c>\uXXXX</c>, so it is safe to print anywhere.
    /// </summary>
    public static string Display(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        StringBuilder builder = new(path.Length);

        int i = 0;

        while (i < path.Length)
        {
            char c = path[i];
            bool pairedSurrogate = char.IsHighSurrogate(c) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1]);

            if (pairedSurrogate)
            {
                builder.Append(c).Append(path[i + 1]);
                i++;
            }
            else if (char.IsControl(c) || char.IsSurrogate(c))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
            else
            {
                builder.Append(c);
            }

            i++;
        }

        return builder.ToString();
    }
}
