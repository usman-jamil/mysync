using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;

namespace MySync.Domain.Publishing;

/// <summary>
/// A publication's public name: 128 random bits from <see cref="RandomNumberGenerator"/>, written
/// in base62 (22 characters). It is the secret part of the URL, and it is <b>never</b> derived
/// from the share ID: nothing public may lead back to the private share.
/// </summary>
public sealed record PublishId
{
    public const int Length = 22;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private PublishId(string value) => Value = value;

    public string Value { get; }

    public static PublishId New() => FromBytes(RandomNumberGenerator.GetBytes(16));

    /// <summary>Exactly 22 base62 characters, as <see cref="New"/> makes them.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out PublishId? id)
    {
        id = null;
        if (value is null || value.Length != Length || !value.All(character => Alphabet.Contains(character, StringComparison.Ordinal)))
        {
            return false;
        }

        id = new PublishId(value);
        return true;
    }

    /// <summary>Base62 of 16 bytes, left-padded with '0' to 22 characters. For <see cref="New"/> and tests.</summary>
    internal static PublishId FromBytes(ReadOnlySpan<byte> bytes)
    {
        var number = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        Span<char> text = stackalloc char[Length];
        text.Fill('0');
        for (int i = Length - 1; i >= 0 && !number.IsZero; i--)
        {
            number = BigInteger.DivRem(number, Alphabet.Length, out BigInteger digit);
            text[i] = Alphabet[(int)digit];
        }

        return new PublishId(new string(text));
    }

    public override string ToString() => Value;
}
