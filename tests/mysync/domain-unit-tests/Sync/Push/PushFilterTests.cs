using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Sync.Push;

/// <summary><c>--max-size</c> parsing, the pattern rules, and <see cref="PushFilter.Decide"/>: boundaries, exclude winning, the reason order.</summary>
public class PushFilterTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 22, 23, 45, 12, TimeSpan.Zero);

    private static SharePath P(string value) => SharePath.Create(value).Value;

    private static Func<SharePath, bool> EndsWith(string suffix) => path => path.Value.EndsWith(suffix, StringComparison.Ordinal);

    [Theory]
    [InlineData("1048576", 1_048_576L)]
    [InlineData("1", 1L)]
    [InlineData("12B", 12L)]
    [InlineData("12b", 12L)]
    [InlineData("1K", 1024L)]
    [InlineData("1KB", 1024L)]
    [InlineData("1KiB", 1024L)]
    [InlineData("1kb", 1024L)]
    [InlineData("500MB", 524_288_000L)]
    [InlineData("500M", 524_288_000L)]
    [InlineData("500mib", 524_288_000L)]
    [InlineData("1.5GB", 1_610_612_736L)]
    [InlineData("1.5 GB", 1_610_612_736L)]
    [InlineData("2G", 2_147_483_648L)]
    [InlineData("2GiB", 2_147_483_648L)]
    [InlineData("1TB", 1_099_511_627_776L)]
    [InlineData("1T", 1_099_511_627_776L)]
    [InlineData("1tib", 1_099_511_627_776L)]
    [InlineData("0.5K", 512L)]
    [InlineData("1.9", 1L)]
    public void Sizes_Should_ParseInEveryUnitAndSpelling(string value, long bytes) =>
        ByteSize.Parse(value).Value.ShouldBe(bytes);

    [Theory]
    [InlineData("0")]
    [InlineData("0MB")]
    [InlineData("0.5")]
    [InlineData("-1MB")]
    [InlineData("MB")]
    [InlineData("1XB")]
    [InlineData("1IB")]
    [InlineData("1.")]
    [InlineData(".5GB")]
    [InlineData("1.2.3MB")]
    [InlineData("")]
    [InlineData("1e6")]
    [InlineData("99999999999TB")]
    [InlineData("99999999999999999999999999999999")]
    public void NonSizes_Should_BeRefused(string value)
    {
        SharedKernel.Error error = ByteSize.Parse(value).Error;

        error.Code.ShouldBe("Push.InvalidSize");
        error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
    }

    [Theory]
    [InlineData("*.log")]
    [InlineData("logs/")]
    [InlineData("app/**/*.txt")]
    [InlineData("/top-level.txt")]
    [InlineData("\\!important.txt")]
    [InlineData("\\#notes")]
    public void Patterns_Should_BeAccepted(string pattern) =>
        PushPatterns.Check(PushPatterns.ExcludeOption, pattern).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("", "empty")]
    [InlineData("  ", "empty")]
    [InlineData("!*.log", "negated")]
    [InlineData("#notes", "comment")]
    [InlineData("a.log\nb.log", "line break")]
    [InlineData("a\tb", "control")]
    public void Patterns_Should_BeRefused_SayingWhy(string pattern, string why)
    {
        SharedKernel.Error error = PushPatterns.Check(PushPatterns.IncludeOption, pattern).Error;

        error.Code.ShouldBe("Push.InvalidPattern");
        error.Description.ShouldStartWith("--include");
        error.Description.ShouldContain(why);
    }

    [Fact]
    public void NoFilter_Should_KeepEverything()
    {
        PushFilter.None.IsActive.ShouldBeFalse();
        PushFilter.None.Decide(P("a.gz"), long.MaxValue, DateTimeOffset.MinValue).ShouldBeNull();
    }

    [Fact]
    public void UpdatedSince_Should_KeepAFileModifiedAtTheCutoff_AndLeaveOutOneATickBefore()
    {
        PushFilter filter = new(updatedSinceGiven: true, Cutoff, null, null, null);

        filter.IsActive.ShouldBeTrue();
        filter.Decide(P("a.log"), 1, Cutoff).ShouldBeNull();
        filter.Decide(P("a.log"), 1, Cutoff.AddTicks(1)).ShouldBeNull();
        filter.Decide(P("a.log"), 1, Cutoff.AddTicks(-1)).ShouldBe(PushFilterReason.NotChanged);
    }

    [Fact]
    public void UpdatedSince_WithoutACutoff_Should_StillBeAFilter_ButKeepEverything()
    {
        PushFilter filter = new(updatedSinceGiven: true, null, null, null, null);

        filter.IsActive.ShouldBeTrue();
        filter.Decide(P("a.log"), 1, DateTimeOffset.MinValue).ShouldBeNull();
    }

    [Fact]
    public void MaxSize_Should_KeepAFileOfExactlyTheLimit_AndLeaveOutOneByteMore()
    {
        PushFilter filter = new(false, null, null, null, 1024);

        filter.IsActive.ShouldBeTrue();
        filter.Decide(P("a.log"), 1024, Cutoff).ShouldBeNull();
        filter.Decide(P("a.log"), 0, Cutoff).ShouldBeNull();
        filter.Decide(P("a.log"), 1025, Cutoff).ShouldBe(PushFilterReason.TooLarge);
    }

    [Fact]
    public void IncludeOnly_Should_KeepOnlyMatches()
    {
        PushFilter filter = new(false, null, EndsWith(".log"), null, null);

        filter.Decide(P("app/a.log"), 1, Cutoff).ShouldBeNull();
        filter.Decide(P("app/a.txt"), 1, Cutoff).ShouldBe(PushFilterReason.NotIncluded);
    }

    [Fact]
    public void ExcludeOnly_Should_LeaveOutMatches()
    {
        PushFilter filter = new(false, null, null, EndsWith(".gz"), null);

        filter.Decide(P("app/a.log"), 1, Cutoff).ShouldBeNull();
        filter.Decide(P("app/a.log.gz"), 1, Cutoff).ShouldBe(PushFilterReason.Excluded);
    }

    [Fact]
    public void Exclude_Should_WinOverInclude()
    {
        PushFilter filter = new(false, null, EndsWith(".log"), EndsWith("debug.log"), null);

        filter.Decide(P("app/server.log"), 1, Cutoff).ShouldBeNull();
        filter.Decide(P("app/debug.log"), 1, Cutoff).ShouldBe(PushFilterReason.Excluded);
    }

    [Fact]
    public void AFileEveryFilterLeavesOut_Should_CountUnderTheFirst_InTheFixedOrder()
    {
        PushFilter all = new(true, Cutoff, EndsWith(".log"), EndsWith(".txt"), 10);
        PushFilter noSince = new(false, null, EndsWith(".log"), EndsWith(".txt"), 10);
        PushFilter onlyLast = new(false, null, null, EndsWith(".txt"), 10);
        DateTimeOffset old = Cutoff.AddDays(-1);

        all.Decide(P("big.txt"), 100, old).ShouldBe(PushFilterReason.NotChanged);
        noSince.Decide(P("big.txt"), 100, old).ShouldBe(PushFilterReason.NotIncluded);
        onlyLast.Decide(P("big.txt"), 100, old).ShouldBe(PushFilterReason.Excluded);
        onlyLast.Decide(P("big.log"), 100, old).ShouldBe(PushFilterReason.TooLarge);
        all.Decide(P("small.log"), 10, Cutoff).ShouldBeNull();
    }

    [Fact]
    public void FilteredOut_Should_CountEachReason()
    {
        var counts = PushFilteredOut.Count([
            PushFilterReason.NotChanged, PushFilterReason.NotChanged, PushFilterReason.Excluded, PushFilterReason.TooLarge, PushFilterReason.NotIncluded, PushFilterReason.NotChanged]);

        counts.ShouldBe(new PushFilteredOut(NotChanged: 3, NotIncluded: 1, Excluded: 1, TooLarge: 1));
        counts.Total.ShouldBe(6);
        PushFilteredOut.None.Total.ShouldBe(0);
    }
}
