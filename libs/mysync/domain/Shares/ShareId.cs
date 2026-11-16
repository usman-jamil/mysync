using System.Diagnostics.CodeAnalysis;

namespace MySync.Domain.Shares;

/// <summary>
/// Identifies a share: a lowercase UUID in "D" form, e.g. <c>0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10</c>
/// (CLAUDE.md §6). It is part of every blob name, so its text form never varies.
/// </summary>
public sealed record ShareId
{
    private const int CanonicalLength = 36;

    private ShareId(Guid value) => Value = value;

    public Guid Value { get; }

    public static ShareId New() => new(Guid.NewGuid());

    /// <summary>
    /// Accepts exactly the 36-character dashed form (any letter case). Braces, missing dashes,
    /// surrounding whitespace and the all-zero UUID are rejected.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ShareId? shareId)
    {
        shareId = null;

        // The length check matters: Guid.TryParseExact silently trims surrounding whitespace.
        if (value is null || value.Length != CanonicalLength || !Guid.TryParseExact(value, "D", out Guid guid) || guid == Guid.Empty)
        {
            return false;
        }

        shareId = new ShareId(guid);
        return true;
    }

    public override string ToString() => Value.ToString("D");
}
