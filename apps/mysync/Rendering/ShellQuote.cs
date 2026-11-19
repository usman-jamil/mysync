using System.Buffers;

namespace MySync.Cli.Rendering;

/// <summary>
/// Quotes a path for a command people paste back into their shell. Single quotes everywhere:
/// POSIX shells close and reopen around an inner quote (<c>'\''</c>); PowerShell doubles it (<c>''</c>).
/// A path of only <c>[A-Za-z0-9._/-]</c> stays bare.
/// </summary>
internal static class ShellQuote
{
    private static readonly SearchValues<char> Safe =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._/-");

    public static string Quote(string value) => Quote(value, OperatingSystem.IsWindows());

    public static string Quote(string value, bool powerShell)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > 0 && !value.AsSpan().ContainsAnyExcept(Safe) && !value.StartsWith('-'))
        {
            return value;
        }

        return powerShell
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
