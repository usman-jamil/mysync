using MySync.Domain.Updates;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("mysync-1.2.3", "1.2.3")]
    [InlineData("mysync-v1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("0.3.0+abc123def", "0.3.0")]
    [InlineData(" 10.20.30 ", "10.20.30")]
    public void TryParse_Should_AcceptTagsAndAssemblyVersions(string text, string expected)
    {
        ReleaseVersion.TryParse(text, out ReleaseVersion? version).ShouldBeTrue();
        version!.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.02.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-beta_1")]
    [InlineData("-1.2.3")]
    public void TryParse_Should_RejectAnythingElse(string? text)
    {
        ReleaseVersion.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ordering_Should_FollowSemanticVersioning()
    {
        string[] ascending = ["0.9.0", "0.10.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];

        List<ReleaseVersion> versions = [.. ascending.Select(Parse)];

        versions.Order().Select(version => version.ToString()).ShouldBe(ascending);
        for (int i = 1; i < versions.Count; i++)
        {
            (versions[i - 1] < versions[i]).ShouldBeTrue($"{versions[i - 1]} < {versions[i]}");
        }
    }

    [Theory]
    [InlineData("0.3.0", "0.3.1", false)]
    [InlineData("0.3.0", "0.4.0", true)]
    [InlineData("0.3.0", "1.3.0", true)]
    [InlineData("0.3.1", "0.3.0", false)]
    public void ASignificantChange_Should_BeAChangeOfMajorOrMinor(string from, string to, bool significant)
    {
        Parse(to).IsSignificantChangeFrom(Parse(from)).ShouldBe(significant);
    }

    private static ReleaseVersion Parse(string text) => ReleaseVersion.TryParse(text, out ReleaseVersion? version) ? version : throw new ArgumentException(text);
}
