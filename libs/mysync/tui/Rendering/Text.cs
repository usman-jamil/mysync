using System.Globalization;
using System.Text;

namespace MySync.Tui.Rendering;

/// <summary>Text helpers for views: widths in cells (one per rune), cutting with an ellipsis, sizes and times.</summary>
public static class Text
{
    public static int Width(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int width = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            width++;
        }

        return width;
    }

    /// <summary>At most <paramref name="width"/> cells, cut at the end with <paramref name="ellipsis"/>.</summary>
    public static string Fit(string text, int width, string ellipsis)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0)
        {
            return string.Empty;
        }

        if (Width(text) <= width)
        {
            return text;
        }

        return Take(text, width - 1) + ellipsis;
    }

    /// <summary>At most <paramref name="width"/> cells, cut from the start (for paths: the end is the useful part).</summary>
    public static string FitLeft(string text, int width, string ellipsis)
    {
        ArgumentNullException.ThrowIfNull(text);
        int length = Width(text);
        if (width <= 0)
        {
            return string.Empty;
        }

        if (length <= width)
        {
            return text;
        }

        Rune[] runes = [.. text.EnumerateRunes()];
        return ellipsis + string.Concat(runes[(length - width + 1)..].Select(rune => rune.ToString()));
    }

    /// <summary><paramref name="text"/> padded with spaces (or cut) to exactly <paramref name="width"/> cells.</summary>
    public static string Pad(string text, int width, string ellipsis)
    {
        string fitted = Fit(text, width, ellipsis);
        return fitted + new string(' ', Math.Max(0, width - Width(fitted)));
    }

    public static string PadLeft(string text, int width, string ellipsis)
    {
        string fitted = Fit(text, width, ellipsis);
        return new string(' ', Math.Max(0, width - Width(fitted))) + fitted;
    }

    /// <summary><c>612 B</c>, <c>4.2 KB</c>, <c>1.3 MB</c>, <c>2.0 GB</c> (1 KB = 1024 B).</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        return value >= 100
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    /// <summary><c>6.1 of 9.8 KB</c> when both are in one unit, else <c>410 KB of 1.3 MB</c>.</summary>
    public static string BytesOf(long done, long total)
    {
        string doneText = Bytes(done);
        string totalText = Bytes(total);
        int space = doneText.LastIndexOf(' ');
        return space > 0 && totalText.EndsWith(doneText[space..], StringComparison.Ordinal)
            ? $"{doneText[..space]} of {totalText}"
            : $"{doneText} of {totalText}";
    }

    /// <summary><c>today 11:02</c>, <c>yesterday 09:41</c>, or <c>2026-11-20 09:41</c>, in UTC.</summary>
    public static string When(DateTimeOffset time, DateTimeOffset now)
    {
        DateTime day = time.UtcDateTime.Date;
        DateTime today = now.UtcDateTime.Date;
        string clock = time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (day == today)
        {
            return $"today {clock}";
        }

        return day == today.AddDays(-1)
            ? $"yesterday {clock}"
            : time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary><c>just now</c>, <c>2 minutes ago</c>, <c>3 hours ago</c>.</summary>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        TimeSpan elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            int minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes ago");
        }

        int hours = (int)elapsed.TotalHours;
        return hours == 1 ? "1 hour ago" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours ago");
    }

    private static string Take(string text, int cells)
    {
        StringBuilder taken = new();
        int count = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (count++ >= cells)
            {
                break;
            }

            taken.Append(rune.ToString());
        }

        return taken.ToString();
    }
}
