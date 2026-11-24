using MySync.Cli.Rendering;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary><c>mysync clone</c>'s folder as shown, and its <c>cd</c> hint (linked from the CLI).</summary>
public class CloneLocationTests
{
    private static readonly string Current = OperatingSystem.IsWindows() ? @"C:\work" : "/home/usman/work";
    private static readonly char Separator = Path.DirectorySeparatorChar;

    [Fact]
    public void AFolderHere_Should_BeShownRelative_AndCdToItsName()
    {
        CloneLocation.Of(Path.Combine(Current, "app-config"), Current, powerShell: false)
            .ShouldBe(($".{Separator}app-config", "app-config"));
    }

    [Fact]
    public void TheCurrentFolder_Should_NeedNoCd()
    {
        CloneLocation.Of(Current, Current, powerShell: false).ShouldBe((".", null));
    }

    [Fact]
    public void AFolderElsewhere_Should_BeShownInFull()
    {
        string elsewhere = OperatingSystem.IsWindows() ? @"D:\deploy\config" : "/srv/deploy/config";

        (string shown, string? cd) = CloneLocation.Of(elsewhere, Current, powerShell: false);

        shown.ShouldBe(elsewhere);
        cd.ShouldBe(OperatingSystem.IsWindows() ? $"'{elsewhere}'" : elsewhere);
    }

    [Fact]
    public void ASiblingFolder_Should_BeShownInFull_NotAsDotDot()
    {
        string sibling = Path.Combine(Path.GetDirectoryName(Current)!, "other");

        CloneLocation.Of(sibling, Current, powerShell: false).Shown.ShouldBe(sibling);
    }

    [Theory]
    [InlineData("my config", false, "'my config'")]
    [InlineData("it's", false, "'it'\\''s'")]
    [InlineData("it's", true, "'it''s'")]
    [InlineData("$HOME", false, "'$HOME'")]
    public void ACdHint_Should_BeQuotedForTheShell(string name, bool powerShell, string expected)
    {
        CloneLocation.Of(Path.Combine(Current, name), Current, powerShell).Cd.ShouldBe(expected);
    }
}
