using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using MySync.Domain.Paths.Normalization;
using SharedKernel;

namespace MySync.Domain.Shares;

/// <summary>
/// A share's human-readable name, e.g. <c>next-wave</c>: what people type instead of the share ID
/// (<c>mysync sync next-wave</c>). It points at a <see cref="ShareId"/> through its alias blob
/// (<c>aliases/&lt;name&gt;</c>, <see cref="Storage.ShareLayout.Alias"/>); the ID stays the storage key.
/// </summary>
/// <remarks>
/// Trimmed and lowercased, then 3–40 of <c>a–z</c>, <c>0–9</c> and <c>-</c>, starting and ending with
/// a letter or digit, and never something that parses as a UUID (so a name can't be mistaken for an
/// ID). The name is a blob path segment, so these rules are also what keeps <c>/</c>, <c>.</c>,
/// <c>..</c>, spaces and control characters out of blob names.
/// </remarks>
public sealed record ShareName
{
    public const int MinLength = 3;
    public const int MaxLength = 40;

    private ShareName(string value) => Value = value;

    public string Value { get; }

    public static Result<ShareName> Create(string? value) =>
        TryCreate(value, out ShareName? name, out string? reason) ? name : Result.Failure<ShareName>(ShareErrors.InvalidName(reason));

    public static bool TryCreate([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ShareName? name, [NotNullWhen(false)] out string? reason)
    {
        name = null;
        string candidate = AsciiLower((value ?? string.Empty).Trim());

        reason = candidate switch
        {
            "" => "it is empty",
            { Length: < MinLength } => $"it is shorter than {MinLength} characters",
            { Length: > MaxLength } => $"it is longer than {MaxLength} characters",
            _ when !candidate.All(IsAllowed) => "it may only contain letters a-z, digits and '-'",
            _ when candidate[0] == '-' || candidate[^1] == '-' => "it must start and end with a letter or digit",
            _ when Guid.TryParse(candidate, out _) => "it looks like a share ID; pick a name that isn't a UUID",
            _ => null,
        };

        if (reason is not null)
        {
            return false;
        }

        name = new ShareName(candidate);
        return true;
    }

    /// <summary>
    /// A name to suggest when a new share has none, from its folder's name: accents stripped,
    /// anything else outside <c>a–z0–9</c> turned into <c>-</c>, repeated dashes collapsed, trimmed,
    /// at most 40 characters. <c>null</c> when that isn't a valid name. Only for the message: a share
    /// is never created with a name nobody chose.
    /// </summary>
    public static ShareName? SuggestFrom(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        StringBuilder text = new();
        foreach (char c in AsciiLower(NfcNormalizer.Decompose(folderName)))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue; // the accent of a decomposed letter
            }

            char next = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-';
            if (next != '-' || text.Length > 0 && text[^1] != '-')
            {
                text.Append(next);
            }
        }

        string suggestion = text.ToString().Trim('-');
        if (suggestion.Length > MaxLength)
        {
            suggestion = suggestion[..MaxLength].TrimEnd('-');
        }

        return TryCreate(suggestion, out ShareName? name, out _) ? name : null;
    }

    public override string ToString() => Value;

    // Only ASCII letters can be in a name, so only they are lowered: no culture rules, nothing else changes.
    private static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] | 0x20) : source[i];
            }
        });

    private static bool IsAllowed(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-';
}
