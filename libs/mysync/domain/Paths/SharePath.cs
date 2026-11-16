using SharedKernel;

namespace MySync.Domain.Paths;

/// <summary>
/// A validated, normalized path relative to the share root (CLAUDE.md §8): NFC, <c>/</c>-separated,
/// no leading or trailing <c>/</c>, no empty, <c>.</c> or <c>..</c> segments, no drive letter,
/// no backslash, no control characters, no unpaired surrogates. The only way to get one is
/// <see cref="Create"/>, so every path in a manifest or plan has passed these checks, including
/// paths read from a remote (possibly crafted) manifest.
/// </summary>
/// <remarks>
/// Backslash is rejected everywhere: Windows treats it as a separator, so <c>a\..\..\x</c> would
/// escape the share root there. A Linux file whose name contains <c>\</c> is skipped by the
/// scanner with a warning. Containment of the final local path in the share root is checked
/// again by Infrastructure when the path is mapped to disk.
/// Equality and ordering are ordinal.
/// </remarks>
public sealed record SharePath
{
    public const char Separator = '/';

    private SharePath(string value) => Value = value;

    public static IComparer<SharePath> OrdinalComparer { get; } =
        Comparer<SharePath>.Create(static (left, right) => string.CompareOrdinal(left?.Value, right?.Value));

    public string Value { get; }

    /// <summary>The last segment.</summary>
    public string Name => Value[(Value.LastIndexOf(Separator) + 1)..];

    /// <summary>The containing directory, or <c>null</c> for a top-level entry.</summary>
    public SharePath? Parent
    {
        get
        {
            int index = Value.LastIndexOf(Separator);
            return index < 0 ? null : new SharePath(Value[..index]);
        }
    }

    public IReadOnlyList<string> Segments => Value.Split(Separator);

    public static Result<SharePath> Create(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result.Failure<SharePath>(PathErrors.Empty());
        }

        Result characters = CheckCharacters(value);
        if (characters.IsFailure)
        {
            return Result.Failure<SharePath>(characters.Error);
        }

        string path = PathRules.ToNfc(value);

        if (path[0] == Separator)
        {
            return Result.Failure<SharePath>(PathErrors.Absolute(path));
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return Result.Failure<SharePath>(PathErrors.DriveLetter(path));
        }

        foreach (string segment in path.Split(Separator))
        {
            if (segment.Length == 0)
            {
                return Result.Failure<SharePath>(PathErrors.EmptySegment(path));
            }

            if (segment is "." or "..")
            {
                return Result.Failure<SharePath>(PathErrors.RelativeSegment(path));
            }
        }

        return new SharePath(path);
    }

    /// <summary>True if this path is strictly below <paramref name="directory"/> (not equal to it).</summary>
    public bool IsUnder(SharePath directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        return Value.Length > directory.Value.Length
            && Value[directory.Value.Length] == Separator
            && Value.StartsWith(directory.Value, StringComparison.Ordinal);
    }

    public override string ToString() => Value;

    private static Result CheckCharacters(string value)
    {
        int i = 0;

        while (i < value.Length)
        {
            char c = value[i];

            if (char.IsControl(c))
            {
                return Result.Failure(PathErrors.ControlCharacter(value));
            }

            if (c == '\\')
            {
                return Result.Failure(PathErrors.Backslash(value));
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return Result.Failure(PathErrors.UnpairedSurrogate(value));
            }

            i++;
        }

        return Result.Success();
    }
}
