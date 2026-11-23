using System.Globalization;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

/// <summary>Which form an <c>--updated-since</c> value took.</summary>
public enum PushSinceKind
{
    /// <summary><c>30m</c>, <c>2h</c>, <c>1d</c>, <c>1w</c>: back from the push's start.</summary>
    Duration,

    /// <summary>An ISO 8601 time or date.</summary>
    Time,

    /// <summary>Since this host's previous push to the share.</summary>
    Last,
}

/// <summary>
/// <c>--updated-since &lt;duration | time | last&gt;</c> (CLAUDE.md §10b): parsed here without a clock
/// (<see cref="Parse"/>, what the validator checks), turned into a cutoff once the push's start time,
/// the local time zone and HEAD are known (<see cref="Resolve"/>). Files modified at or after the
/// cutoff are pushed.
/// </summary>
public sealed record PushSince
{
    /// <summary>The keyword for "since this host's previous push".</summary>
    public const string LastKeyword = "last";

    // ISO 8601 as people type it: a date, or a date and time to the minute, second or fraction.
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    private PushSince(string spec, PushSinceKind kind, TimeSpan duration, DateTime localTime, DateTimeOffset? time)
    {
        Spec = spec;
        Kind = kind;
        Duration = duration;
        LocalTime = localTime;
        Time = time;
    }

    /// <summary>The value as typed.</summary>
    public string Spec { get; }

    public PushSinceKind Kind { get; }

    /// <summary>For <see cref="PushSinceKind.Duration"/>.</summary>
    public TimeSpan Duration { get; }

    /// <summary>For a <see cref="PushSinceKind.Time"/> typed without an offset: a local wall-clock time.</summary>
    public DateTime LocalTime { get; }

    /// <summary>For a <see cref="PushSinceKind.Time"/> typed with an offset (or <c>Z</c>).</summary>
    public DateTimeOffset? Time { get; }

    /// <summary>The value's shape only; a time in the future is found by <see cref="Resolve"/>, which knows the clock.</summary>
    public static Result<PushSince> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string text = value.Trim();
        if (text.Length == 0)
        {
            return Result.Failure<PushSince>(PushErrors.InvalidSince(value, "is empty"));
        }

        if (string.Equals(text, LastKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return new PushSince(value, PushSinceKind.Last, TimeSpan.Zero, default, null);
        }

        if (TryParseDuration(text, out TimeSpan duration))
        {
            return new PushSince(value, PushSinceKind.Duration, duration, default, null);
        }

        string upper = text.ToUpperInvariant();
        string withOffset = upper.EndsWith('Z') ? $"{upper[..^1]}+00:00" : upper;
        if (DateTimeOffset.TryParseExact(withOffset, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time))
        {
            return new PushSince(value, PushSinceKind.Time, TimeSpan.Zero, default, time);
        }

        if (DateTime.TryParseExact(upper, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return new PushSince(value, PushSinceKind.Time, TimeSpan.Zero, DateTime.SpecifyKind(local, DateTimeKind.Unspecified), null);
        }

        return Result.Failure<PushSince>(PushErrors.InvalidSince(value));
    }

    /// <summary>
    /// The cutoff, or <c>null</c> for none (<c>last</c> with no earlier push from this host, or a
    /// share that doesn't exist yet). Resolved once per push; it doesn't move on a commit retry.
    /// </summary>
    /// <param name="startedUtc">When the push started.</param>
    /// <param name="localZone">For a time typed without an offset.</param>
    /// <param name="sharePaths">HEAD's entries (for <c>last</c>); <c>null</c> when the share doesn't exist yet.</param>
    /// <param name="host">The sanitised host segment this push uses (<see cref="PushTarget.Host"/>).</param>
    public Result<DateTimeOffset?> Resolve(DateTimeOffset startedUtc, TimeZoneInfo localZone, IEnumerable<SharePath>? sharePaths, string host)
    {
        ArgumentNullException.ThrowIfNull(localZone);
        ArgumentNullException.ThrowIfNull(host);

        switch (Kind)
        {
            case PushSinceKind.Last:
                return sharePaths is null ? Result.Success<DateTimeOffset?>(null) : Result.Success(LastPushStart(sharePaths, host));

            case PushSinceKind.Duration:
                return Duration > startedUtc - DateTimeOffset.MinValue
                    ? Result.Failure<DateTimeOffset?>(PushErrors.InvalidSince(Spec, "reaches back too far"))
                    : Result.Success<DateTimeOffset?>(startedUtc.ToUniversalTime() - Duration);

            default:
                DateTimeOffset cutoff = (Time ?? new DateTimeOffset(LocalTime, localZone.GetUtcOffset(LocalTime))).ToUniversalTime();
                return cutoff > startedUtc
                    ? Result.Failure<DateTimeOffset?>(PushErrors.InvalidSince(Spec, "is in the future"))
                    : Result.Success<DateTimeOffset?>(cutoff);
        }
    }

    /// <summary>
    /// When this host's newest push into the share started: the time in the name of the newest
    /// <c>&lt;host&gt;/&lt;timestamp&gt;[-n]</c> folder in HEAD, read from the folder name (no download,
    /// no local state; both ends are this machine's clock). <c>null</c> when there is none.
    /// </summary>
    public static DateTimeOffset? LastPushStart(IEnumerable<SharePath> sharePaths, string host)
    {
        ArgumentNullException.ThrowIfNull(sharePaths);
        ArgumentNullException.ThrowIfNull(host);

        string prefix = host + SharePath.Separator;
        DateTimeOffset? newest = null;

        foreach (SharePath path in sharePaths)
        {
            if (!path.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = path.Value[prefix.Length..];
            int slash = rest.IndexOf(SharePath.Separator, StringComparison.Ordinal);
            string folder = slash < 0 ? rest : rest[..slash];

            if (PushTarget.TryParseFolderName(folder, out DateTimeOffset started) && (newest is null || started > newest))
            {
                newest = started;
            }
        }

        return newest;
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (text.Length < 2 || text[0] is < '1' or > '9' || text.AsSpan(0, text.Length - 1).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        if (!long.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long count))
        {
            return false;
        }

        long minutesPerUnit = char.ToLowerInvariant(text[^1]) switch
        {
            'm' => 1,
            'h' => 60,
            'd' => 60 * 24,
            'w' => 60 * 24 * 7,
            _ => 0,
        };

        if (minutesPerUnit == 0 || count > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMinute / minutesPerUnit)
        {
            return false;
        }

        duration = TimeSpan.FromMinutes(count * minutesPerUnit);
        return true;
    }
}
