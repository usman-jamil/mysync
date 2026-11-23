using System.Globalization;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

/// <summary>
/// <c>--max-size</c> (CLAUDE.md §10b): a positive whole or decimal number and an optional binary
/// unit, case-insensitive: <c>B</c>, <c>K</c>/<c>KB</c>/<c>KiB</c>, <c>M</c>…, <c>G</c>…, <c>T</c>…
/// (1 KB = 1024 B); no unit means bytes. A fraction of a byte is dropped; less than one byte is refused.
/// </summary>
public static class ByteSize
{
    public static Result<long> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string text = value.Trim();
        int numberEnd = 0;
        while (numberEnd < text.Length && (char.IsAsciiDigit(text[numberEnd]) || text[numberEnd] == '.'))
        {
            numberEnd++;
        }

        string number = text[..numberEnd];
        string unit = text[numberEnd..].TrimStart().ToUpperInvariant();

        if (number.Length == 0
            || !char.IsAsciiDigit(number[0])
            || !char.IsAsciiDigit(number[^1])
            || !decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount)
            || Multiplier(unit) is not { } multiplier)
        {
            return Result.Failure<long>(PushErrors.InvalidSize(value));
        }

        if (amount > (decimal)long.MaxValue / multiplier)
        {
            return Result.Failure<long>(PushErrors.InvalidSize(value));
        }

        decimal bytes = decimal.Floor(amount * multiplier);
        return bytes < 1
            ? Result.Failure<long>(PushErrors.InvalidSize(value))
            : Result.Success((long)bytes);
    }

    private static long? Multiplier(string unit) => unit switch
    {
        "" or "B" => 1L,
        "K" or "KB" or "KIB" => 1L << 10,
        "M" or "MB" or "MIB" => 1L << 20,
        "G" or "GB" or "GIB" => 1L << 30,
        "T" or "TB" or "TIB" => 1L << 40,
        _ => null,
    };
}
