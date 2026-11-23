using System.Globalization;
using System.Text;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

/// <summary>
/// Where one push lands in the share: <c>&lt;host&gt;/&lt;UTC start time&gt;/</c>, new on every run.
/// If that exact folder is taken (two pushes from one host in one second), the next candidates are
/// <c>…-2</c>, <c>…-3</c> and so on (<see cref="Candidate"/>). The host segment is sanitised so the
/// target is a valid share path that Windows can hold too.
/// </summary>
public sealed record PushTarget
{
    /// <summary>No colons, so it is a valid folder name on Windows; sorts chronologically.</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss'Z'";

    /// <summary>What an empty host name becomes.</summary>
    public const string FallbackHost = "host";

    private const string InvalidHostCharacters = "<>:\"|?*/\\";

    private PushTarget(string host, string timestamp)
    {
        Host = host;
        Timestamp = timestamp;
        HostFolder = SharePath.Create(host).Value;
    }

    /// <summary>The sanitised host segment.</summary>
    public string Host { get; }

    /// <summary>The start time as <see cref="TimestampFormat"/>.</summary>
    public string Timestamp { get; }

    /// <summary>The host's folder at the share root: <c>web01</c>.</summary>
    public SharePath HostFolder { get; }

    /// <param name="host">The machine name, or <c>--host</c>; sanitised here.</param>
    /// <param name="startedUtc">When the push started.</param>
    public static Result<PushTarget> Create(string host, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(host);

        string segment = SanitizeHost(host);
        string timestamp = startedUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        Result<SharePath> path = SharePath.Create($"{segment}{SharePath.Separator}{timestamp}");
        if (path.IsFailure)
        {
            return Result.Failure<PushTarget>(PushErrors.InvalidTarget(path.Error.Description));
        }

        Result windows = PathRules.CheckWindowsCompatible(path.Value);
        if (windows.IsFailure)
        {
            return Result.Failure<PushTarget>(PushErrors.InvalidTarget(
                $"'{PathErrors.Display(segment)}' isn't a name every machine can hold ({windows.Error.Description}). Pass --host <another name>."));
        }

        return new PushTarget(segment, timestamp);
    }

    /// <summary>
    /// Every character a share path segment or a Windows file name can't hold becomes <c>-</c>
    /// (<c>&lt;&gt;:"|?*/\</c>, control characters, unpaired surrogates); leading and trailing white
    /// space and trailing dots are trimmed; an empty result is <see cref="FallbackHost"/>. The result
    /// is NFC. Reserved device names (<c>CON</c>) are not renamed: <see cref="Create"/> refuses them.
    /// </summary>
    public static string SanitizeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        StringBuilder text = new(host.Length);
        int i = 0;
        while (i < host.Length)
        {
            char c = host[i++];
            if (char.IsHighSurrogate(c) && i < host.Length && char.IsLowSurrogate(host[i]))
            {
                text.Append(c).Append(host[i++]);
            }
            else if (char.IsSurrogate(c) || char.IsControl(c) || InvalidHostCharacters.Contains(c, StringComparison.Ordinal))
            {
                text.Append('-');
            }
            else
            {
                text.Append(c);
            }
        }

        string trimmed = PathRules.ToNfc(text.ToString()).Trim().TrimEnd('.', ' ');
        return trimmed.Length == 0 ? FallbackHost : trimmed;
    }

    /// <summary>The <paramref name="number"/>-th candidate folder: 1 is <c>host/timestamp</c>, 2 is <c>host/timestamp-2</c>.</summary>
    public SharePath Candidate(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);

        string name = number == 1 ? Timestamp : string.Create(CultureInfo.InvariantCulture, $"{Timestamp}-{number}");
        return SharePath.Create($"{Host}{SharePath.Separator}{name}").Value;
    }

    /// <summary>
    /// Reads a target folder's own name (the segment under the host folder): <c>20261121-051207Z</c>
    /// or a suffixed <c>20261121-051207Z-2</c>, as <see cref="Candidate"/> writes them. Anything
    /// else (a folder someone made by hand) is not a push folder.
    /// </summary>
    /// <param name="startedUtc">The start time the name records, to the second.</param>
    public static bool TryParseFolderName(string name, out DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(name);
        startedUtc = default;

        int timestampLength = "yyyyMMdd-HHmmssZ".Length;
        if (name.Length < timestampLength)
        {
            return false;
        }

        string suffix = name[timestampLength..];
        if (suffix.Length > 0 && !IsSuffix(suffix))
        {
            return false;
        }

        if (!DateTime.TryParseExact(name[..timestampLength], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
        {
            return false;
        }

        startedUtc = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    public override string ToString() => Candidate(1).Value;

    // "-2", "-3", …: what Candidate appends; never "-1" or a leading zero.
    private static bool IsSuffix(string suffix) =>
        suffix.Length >= 2
        && suffix[0] == '-'
        && suffix[1] is >= '1' and <= '9'
        && !suffix.AsSpan(1).ContainsAnyExceptInRange('0', '9')
        && suffix != "-1";
}
