using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MySync.Domain.Updates;

/// <summary>
/// A mysync release version: <c>MAJOR.MINOR.PATCH</c> with an optional <c>-prerelease</c> suffix,
/// as the release workflow accepts (CLAUDE.md §16). Ordered by Semantic Versioning 2.0: a
/// prerelease sorts before its release, and prerelease identifiers compare numerically where both
/// are numbers. Build metadata (<c>+abc123</c>) is ignored.
/// </summary>
public sealed record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The part after <c>-</c>, e.g. <c>beta.1</c>; <c>null</c> for a release.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => Prerelease is not null;

    /// <summary>
    /// Accepts <c>1.2.3</c>, <c>v1.2.3</c>, a tag such as <c>mysync-1.2.3</c> or <c>mysync-v1.2.3-beta.1</c>,
    /// and an assembly version with build metadata (<c>1.2.3+abc</c>).
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.StartsWith(ReleaseAssets.TagPrefix, StringComparison.Ordinal))
        {
            value = value[ReleaseAssets.TagPrefix.Length..];
        }

        if (value.StartsWith('v'))
        {
            value = value[1..];
        }

        int plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? prerelease = null;
        int dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0 || !prerelease.Split('.').All(IsIdentifier))
            {
                return false;
            }
        }

        string[] parts = value.Split('.');
        if (parts.Length != 3
            || !TryNumber(parts[0], out int major)
            || !TryNumber(parts[1], out int minor)
            || !TryNumber(parts[2], out int patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>
    /// A release that people should read the notes for before installing: the major or minor number
    /// changed (e.g. 0.3 → 0.4 changed a shared format once). Every machine in a team should update together.
    /// </summary>
    public bool IsSignificantChangeFrom(ReleaseVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Major != other.Major || Minor != other.Minor;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,  // 1.0.0 > 1.0.0-beta
            (_, null) => -1,
            _ => ComparePrerelease(Prerelease!, other.Prerelease!),
        };
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => Compare(left, right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => Compare(left, right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => Compare(left, right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => Compare(left, right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}{(Prerelease is null ? string.Empty : "-" + Prerelease)}");

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }

    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumber = TryNumber(a[i], out int x);
            bool bNumber = TryNumber(b[i], out int y);
            int order = (aNumber, bNumber) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1, // numeric identifiers sort before alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };

            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && (text.Length == 1 || text[0] != '0');

    private static bool IsIdentifier(string identifier) =>
        identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
