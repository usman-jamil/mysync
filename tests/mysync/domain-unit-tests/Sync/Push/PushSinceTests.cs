using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Sync.Push;

/// <summary><c>--updated-since</c>: durations, ISO times, and <c>last</c> read from HEAD's folder names.</summary>
public class PushSinceTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    // A fixed zone, UTC+5, so "local" doesn't depend on the machine running the tests.
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.CreateCustomTimeZone("test+5", TimeSpan.FromHours(5), "test+5", "test+5");

    private static DateTimeOffset? Cutoff(string value, IEnumerable<string>? share = null, string host = "web01") =>
        PushSince.Parse(value).Value.Resolve(Started, Karachi, share?.Select(path => SharePath.Create(path).Value), host).Value;

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("1w", 10080)]
    [InlineData("2H", 120)]
    [InlineData(" 90m ", 90)]
    [InlineData("1000d", 1_440_000)]
    public void Durations_Should_CountBackFromTheStart(string value, int minutes)
    {
        PushSince since = PushSince.Parse(value).Value;

        since.Kind.ShouldBe(PushSinceKind.Duration);
        since.Spec.ShouldBe(value);
        Cutoff(value).ShouldBe(Started.AddMinutes(-minutes));
    }

    [Theory]
    [InlineData("2026-11-23T04:00:00Z", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-23t04:00:00z", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-23T04:00Z", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-23T04:00:00.5Z", "2026-11-23T04:00:00.5+00:00")]
    [InlineData("2026-11-23T09:00:00+05:00", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-22T23:00:00-05:00", "2026-11-23T04:00:00+00:00")]
    public void TimesWithAnOffset_Should_BeThatInstant(string value, string expected)
    {
        PushSince.Parse(value).Value.Kind.ShouldBe(PushSinceKind.Time);
        Cutoff(value).ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2026-11-23T09:00:00", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-23T09:00", "2026-11-23T04:00:00+00:00")]
    [InlineData("2026-11-23", "2026-11-22T19:00:00+00:00")] // local midnight
    public void TimesWithoutAnOffset_Should_BeLocalTime(string value, string expected) =>
        Cutoff(value).ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("2026-11-23T06:00:01Z")]
    [InlineData("2026-11-24")]
    [InlineData("2027-01-01T00:00:00+14:00")]
    public void ATimeInTheFuture_Should_BeRefused(string value)
    {
        SharedKernel.Error error = PushSince.Parse(value).Value.Resolve(Started, Karachi, null, "web01").Error;

        error.Code.ShouldBe("Push.InvalidSince");
        error.Description.ShouldContain("in the future");
    }

    [Fact]
    public void TheStartItself_Should_BeAllowed() =>
        Cutoff("2026-11-23T06:00:00Z").ShouldBe(Started);

    [Theory]
    [InlineData("2x")]
    [InlineData("-1h")]
    [InlineData("0h")]
    [InlineData("01h")]
    [InlineData("1.5h")]
    [InlineData("h")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yesterday")]
    [InlineData("lastt")]
    [InlineData("2026-13-01")]
    [InlineData("2026-11-23 04:00")]
    [InlineData("99999999999999999999m")]
    public void Nonsense_Should_BeRefused_ListingTheAcceptedForms(string value)
    {
        SharedKernel.Error error = PushSince.Parse(value).Error;

        error.Code.ShouldBe("Push.InvalidSince");
        error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
        error.Description.ShouldContain("30m");
        error.Description.ShouldContain("last");
    }

    [Fact]
    public void ADurationReachingBeforeYearOne_Should_BeRefused() =>
        PushSince.Parse("200000w").Value.Resolve(Started, Karachi, null, "web01").Error.Code.ShouldBe("Push.InvalidSince");

    [Theory]
    [InlineData("last")]
    [InlineData("LAST")]
    [InlineData("Last")]
    public void Last_Should_BeCaseInsensitive(string value) =>
        PushSince.Parse(value).Value.Kind.ShouldBe(PushSinceKind.Last);

    [Fact]
    public void Last_Should_BeTheNewestFolderOfThisHost()
    {
        string[] share =
        [
            "docs/readme.md",
            "web01/20261121-051207Z/a.log",
            "web01/20261122-235512Z/a.log",
            "web01/20261122-235512Z/sub/b.log",
            "web01/20261120-000000Z/a.log",
            "web02/20261123-050000Z/a.log",
        ];

        Cutoff("last", share).ShouldBe(new DateTimeOffset(2026, 11, 22, 23, 55, 12, TimeSpan.Zero));
    }

    [Fact]
    public void Last_Should_CountSuffixedFolders()
    {
        string[] share = ["web01/20261122-235512Z/a.log", "web01/20261122-235512Z-2/a.log", "web01/20261123-010000Z-3/a.log"];

        Cutoff("last", share).ShouldBe(new DateTimeOffset(2026, 11, 23, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Last_Should_FollowTheHostSegment_SoHostSelectsTheHistory()
    {
        string[] share = ["web01/20261122-000000Z/a.log", "collector/20261123-010000Z/a.log", "web/20261123-020000Z/a.log"];

        Cutoff("last", share, host: "collector").ShouldBe(new DateTimeOffset(2026, 11, 23, 1, 0, 0, TimeSpan.Zero));
        Cutoff("last", share, host: "web01").ShouldBe(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Last_Should_IgnoreFoldersThatArentPushFolders()
    {
        string[] share = ["web01/notes/a.txt", "web01/20261122-000000Z-1/a.log", "web01/20261122-000000Z-02/a.log", "web01/2026112-000000Z/a.log", "web01/readme.txt"];

        Cutoff("last", share).ShouldBeNull();
    }

    [Fact]
    public void Last_WithNoEarlierPush_Should_HaveNoCutoff()
    {
        Cutoff("last", ["web02/20261123-050000Z/a.log", "docs/readme.md"]).ShouldBeNull();
        Cutoff("last", []).ShouldBeNull();
    }

    [Fact]
    public void Last_ForAShareThatDoesntExistYet_Should_HaveNoCutoff() =>
        PushSince.Parse("last").Value.Resolve(Started, Karachi, null, "web01").Value.ShouldBeNull();

    [Fact]
    public void Last_Should_CountAnEmptyPushFolderEntry() =>
        Cutoff("last", ["web01/20261122-000000Z"]).ShouldBe(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("20261121-051207Z", true)]
    [InlineData("20261121-051207Z-2", true)]
    [InlineData("20261121-051207Z-17", true)]
    [InlineData("20261121-051207Z-1", false)]
    [InlineData("20261121-051207Z-02", false)]
    [InlineData("20261121-051207Z-", false)]
    [InlineData("20261121-051207Z-x", false)]
    [InlineData("20261121-051207", false)]
    [InlineData("20261321-051207Z", false)]
    [InlineData("notes", false)]
    public void FolderNames_Should_BeReadOnlyInTheFormPushWritesThem(string name, bool isPushFolder)
    {
        PushTarget.TryParseFolderName(name, out DateTimeOffset started).ShouldBe(isPushFolder);
        if (isPushFolder)
        {
            started.ShouldBe(new DateTimeOffset(2026, 11, 21, 5, 12, 7, TimeSpan.Zero));
        }
    }

    [Fact]
    public void EveryCandidate_Should_ParseBackToItsStart()
    {
        PushTarget target = PushTarget.Create("web01", Started).Value;

        foreach (int number in new[] { 1, 2, 3, 10, 125 })
        {
            PushTarget.TryParseFolderName(target.Candidate(number).Name, out DateTimeOffset started).ShouldBeTrue();
            started.ShouldBe(Started);
        }
    }
}
