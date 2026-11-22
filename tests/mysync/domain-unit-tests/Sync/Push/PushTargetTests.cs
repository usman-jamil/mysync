using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Sync.Push;

/// <summary>The target folder <c>&lt;host&gt;/&lt;UTC start&gt;/</c>: its form, the host sanitising and the suffixes.</summary>
public class PushTargetTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 21, 5, 12, 7, TimeSpan.Zero);

    [Fact]
    public void Target_Should_BeHostSlashUtcStartTime()
    {
        PushTarget target = PushTarget.Create("web01", Started).Value;

        target.Host.ShouldBe("web01");
        target.Timestamp.ShouldBe("20261121-051207Z");
        target.HostFolder.Value.ShouldBe("web01");
        target.Candidate(1).Value.ShouldBe("web01/20261121-051207Z");
        target.ToString().ShouldBe("web01/20261121-051207Z");
    }

    [Fact]
    public void Timestamp_Should_BeUtc_WhateverTheOffset()
    {
        PushTarget target = PushTarget.Create("web01", new DateTimeOffset(2026, 11, 21, 7, 12, 7, TimeSpan.FromHours(2))).Value;

        target.Timestamp.ShouldBe("20261121-051207Z");
    }

    [Fact]
    public void Timestamps_Should_SortChronologically()
    {
        string[] stamps = [.. new[] { Started.AddDays(40), Started, Started.AddSeconds(1), Started.AddHours(-30) }
            .Select(time => PushTarget.Create("h1", time).Value.Timestamp)];

        stamps.Order(StringComparer.Ordinal).ShouldBe([stamps[3], stamps[1], stamps[2], stamps[0]]);
    }

    [Fact]
    public void Candidates_Should_AppendDashNumbers_FromTwo()
    {
        PushTarget target = PushTarget.Create("web01", Started).Value;

        target.Candidate(2).Value.ShouldBe("web01/20261121-051207Z-2");
        target.Candidate(3).Value.ShouldBe("web01/20261121-051207Z-3");
        target.Candidate(12).Value.ShouldBe("web01/20261121-051207Z-12");
        Should.Throw<ArgumentOutOfRangeException>(() => target.Candidate(0));
    }

    [Theory]
    [InlineData("web01", "web01")]
    [InlineData("web 01", "web 01")] // a space is a valid name character
    [InlineData("  web01  ", "web01")]
    [InlineData("db:5432", "db-5432")]
    [InlineData(@"CORP\web01", "CORP-web01")]
    [InlineData("a/b", "a-b")]
    [InlineData("<>:\"|?*", "-------")]
    [InlineData("tab\there", "tab-here")]
    [InlineData("web01.", "web01")]
    [InlineData("web01. . ", "web01")]
    [InlineData("", "host")]
    [InlineData("   ", "host")]
    [InlineData(".", "host")]
    [InlineData("..", "host")]
    [InlineData("serveur-é", "serveur-é")]
    [InlineData("服务器", "服务器")]
    public void Host_Should_BeSanitised(string host, string expected)
    {
        PushTarget.SanitizeHost(host).ShouldBe(expected);
        PushTarget.Create(host, Started).Value.Host.ShouldBe(expected);
    }

    [Fact]
    public void Host_Should_BeNfc()
    {
        PushTarget.SanitizeHost("serveur-é").ShouldBe("serveur-é");
    }

    [Fact]
    public void UnpairedSurrogates_Should_BeReplaced_PairsKept()
    {
        PushTarget.SanitizeHost("a\ud800b").ShouldBe("a-b");
        PushTarget.SanitizeHost("a😀b").ShouldBe("a😀b");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9.local")]
    public void ReservedWindowsNames_Should_BeAnInvalidTarget(string host)
    {
        SharedKernel.Error error = PushTarget.Create(host, Started).Error;

        error.Code.ShouldBe("Push.InvalidTarget");
        error.Description.ShouldContain("--host");
    }
}
