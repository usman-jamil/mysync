using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MySync.Domain.Manifests;

/// <summary>
/// A manifest revision. A new share starts at 1 and every commit adds exactly 1 (CLAUDE.md §6).
/// </summary>
public sealed record Revision : IComparable<Revision>
{
    private Revision(long value) => Value = value;

    public static Revision Initial { get; } = new(1);

    public long Value { get; }

    public static bool TryCreate(long value, [NotNullWhen(true)] out Revision? revision)
    {
        revision = value >= 1 ? new Revision(value) : null;
        return revision is not null;
    }

    public static bool operator <(Revision left, Revision right) => Compare(left, right) < 0;

    public static bool operator >(Revision left, Revision right) => Compare(left, right) > 0;

    public static bool operator <=(Revision left, Revision right) => Compare(left, right) <= 0;

    public static bool operator >=(Revision left, Revision right) => Compare(left, right) >= 0;

    public Revision Next() => new(checked(Value + 1));

    public int CompareTo(Revision? other) => other is null ? 1 : Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    private static int Compare(Revision? left, Revision? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }
}
