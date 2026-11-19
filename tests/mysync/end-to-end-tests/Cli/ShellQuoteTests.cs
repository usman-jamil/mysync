using MySync.Cli.Rendering;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>The paths in printed <c>mysync resolve</c> / <c>mysync diff</c> commands paste back as they are.</summary>
public sealed class ShellQuoteTests
{
    [Theory]
    [InlineData("reports/q3.csv")]
    [InlineData("a-b_c.d/E9")]
    public void ASafePath_Should_StayBare(string path)
    {
        ShellQuote.Quote(path, powerShell: false).ShouldBe(path);
        ShellQuote.Quote(path, powerShell: true).ShouldBe(path);
    }

    [Theory]
    [InlineData("my file.txt", "'my file.txt'", "'my file.txt'")]
    [InlineData("it's.txt", "'it'\\''s.txt'", "'it''s.txt'")]
    [InlineData("$HOME.txt", "'$HOME.txt'", "'$HOME.txt'")]
    [InlineData("`whoami`.txt", "'`whoami`.txt'", "'`whoami`.txt'")]
    [InlineData("a;rm -rf x", "'a;rm -rf x'", "'a;rm -rf x'")]
    [InlineData("café/naïve.txt", "'café/naïve.txt'", "'café/naïve.txt'")]
    [InlineData("-n", "'-n'", "'-n'")]
    [InlineData("", "''", "''")]
    public void OtherPaths_Should_BeSingleQuoted(string path, string posix, string powerShell)
    {
        ShellQuote.Quote(path, powerShell: false).ShouldBe(posix);
        ShellQuote.Quote(path, powerShell: true).ShouldBe(powerShell);
    }

    [Theory]
    [InlineData("it's here.txt")]
    [InlineData("$(touch pwned) `x` ; | & > \" \\ *")]
    [InlineData("''''")]
    public void APosixQuotedPath_Should_ComeBackExactly_FromSh(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no sh on Windows");

        System.Diagnostics.ProcessStartInfo start = new("sh") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf %s " + ShellQuote.Quote(path, powerShell: false));
        using System.Diagnostics.Process sh = System.Diagnostics.Process.Start(start)!;
        string echoed = sh.StandardOutput.ReadToEnd();
        sh.WaitForExit();

        echoed.ShouldBe(path);
    }
}
