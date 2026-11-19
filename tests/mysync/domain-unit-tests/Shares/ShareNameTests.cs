using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Shares;

public class ShareNameTests
{
    [Theory]
    [InlineData("next-wave", "next-wave")]
    [InlineData("abc", "abc")]
    [InlineData("a1b", "a1b")]
    [InlineData("q3", null)]
    [InlineData("Q3-Reports", "q3-reports")]
    [InlineData("  next-wave  ", "next-wave")]
    [InlineData("NEXT-WAVE", "next-wave")]
    [InlineData("a-b-c", "a-b-c")]
    [InlineData("a--b", "a--b")]
    [InlineData("123", "123")]
    [InlineData("2026-q3", "2026-q3")]
    public void Create_Should_TrimAndLowercase_ThenApplyTheRules(string input, string? expected)
    {
        Result<ShareName> name = ShareName.Create(input);

        if (expected is null)
        {
            name.IsFailure.ShouldBeTrue();
        }
        else
        {
            name.Value.Value.ShouldBe(expected);
        }
    }

    [Fact]
    public void Length_Should_Be3To40_AtTheBoundaries()
    {
        ShareName.Create("ab").IsFailure.ShouldBeTrue();
        ShareName.Create("abc").IsSuccess.ShouldBeTrue();
        ShareName.Create(new string('a', 40)).IsSuccess.ShouldBeTrue();
        ShareName.Create(new string('a', 41)).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData(null, "empty")]
    [InlineData("-abc", "start and end")]
    [InlineData("abc-", "start and end")]
    [InlineData("---", "start and end")]
    [InlineData("café", "only contain")]
    [InlineData("日本語の名前", "only contain")]
    [InlineData("a_b", "only contain")]
    [InlineData("a.b", "only contain")]
    [InlineData("a b", "only contain")]
    [InlineData("10a4ab35-936b-4930-96a5-d668f2812661", "share ID")]
    [InlineData("10A4AB35-936B-4930-96A5-D668F2812661", "share ID")]
    [InlineData("10a4ab35936b493096a5d668f2812661", "share ID")]
    public void Create_Should_Refuse_AndSayWhy_WithoutEchoingTheInput(string? input, string because)
    {
        Error error = ShareName.Create(input).Error;

        error.Code.ShouldBe("Shares.InvalidName");
        error.Type.ShouldBe(ErrorType.Validation);
        error.Description.ShouldContain(because);
        if (!string.IsNullOrWhiteSpace(input))
        {
            error.Description.ShouldNotContain(input.Trim(), Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("abc/..")]
    [InlineData("a\\b")]
    [InlineData("abc\u0000")]
    [InlineData("ab\tc")]
    [InlineData("ab\nc")]
    [InlineData("abc‮")]
    [InlineData("%2e%2e")]
    [InlineData("abc?x")]
    [InlineData("abc#x")]
    public void NothingThatCouldChangeABlobPath_Should_GetThrough(string input)
    {
        ShareName.Create(input).IsFailure.ShouldBeTrue();
        ShareName.TryCreate(input, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Q3 Reports", "q3-reports")]
    [InlineData("next_wave", "next-wave")]
    [InlineData("Über Café", "uber-cafe")]
    [InlineData("Crème brûlée (final)", "creme-brulee-final")]
    [InlineData("--weird...name--", "weird-name")]
    [InlineData("a   b\t\tc", "a-b-c")]
    [InlineData("project", "project")]
    [InlineData("2026", "2026")]
    public void SuggestFrom_Should_MakeAValidName_FromAFolderName(string folder, string expected) =>
        ShareName.SuggestFrom(folder)!.Value.ShouldBe(expected);

    [Theory]
    [InlineData("日本語")]
    [InlineData("Ωμέγα")]
    [InlineData("ab")]
    [InlineData("!!!")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("10a4ab35-936b-4930-96a5-d668f2812661")]
    public void SuggestFrom_Should_GiveNothing_WhenNoValidNameComesOut(string? folder) =>
        ShareName.SuggestFrom(folder).ShouldBeNull();

    [Fact]
    public void SuggestFrom_Should_CapAt40_WithoutATrailingDash()
    {
        ShareName suggestion = ShareName.SuggestFrom(new string('a', 39) + " bbbbb")!;

        suggestion.Value.ShouldBe(new string('a', 39));
        ShareName.SuggestFrom(new string('x', 60))!.Value.Length.ShouldBe(40);
    }

    [Fact]
    public void Reference_Should_BeAnId_WhenItParsesAsOne_ElseAName()
    {
        ShareReference byId = Ok(ShareReference.Parse(ShareIdText));
        byId.Id.ShouldBe(Share());
        byId.Name.ShouldBeNull();

        ShareReference byUppercaseId = Ok(ShareReference.Parse(ShareIdText.ToUpperInvariant()));
        byUppercaseId.Id.ShouldBe(Share());

        ShareReference byName = Ok(ShareReference.Parse("Next-Wave"));
        byName.Name!.Value.ShouldBe("next-wave");
        byName.Id.ShouldBeNull();
        byName.ToString().ShouldBe("next-wave");

        foreach (string bad in (string[])["", "a", "../x", "{" + ShareIdText + "}", "00000000-0000-0000-0000-000000000000"])
        {
            ShareReference.Parse(bad).Error.Code.ShouldBe("Shares.InvalidReference");
        }
    }

    [Fact]
    public void AliasBlobs_Should_LiveUnderTheirOwnPrefix()
    {
        ShareName name = Ok(ShareName.Create("next-wave"));

        ShareLayout.AliasesPrefix.ShouldBe("aliases/");
        ShareLayout.Alias(name).ShouldBe("aliases/next-wave");
        ShareLayout.Alias(name).ShouldNotStartWith(ShareLayout.For(Share()).Prefix);

        ShareLayout.TryParseAlias("aliases/next-wave", out ShareName? parsed).ShouldBeTrue();
        parsed.ShouldBe(name);
        foreach (string stray in (string[])["aliases/Next-Wave", "aliases/x", "aliases/next-wave/extra", "aliases/", "shares/next-wave", "aliases/a.b"])
        {
            ShareLayout.TryParseAlias(stray, out _).ShouldBeFalse(stray);
        }
    }

    [Fact]
    public void TheNewErrors_Should_HaveTheirTypesAndMessages()
    {
        ShareName name = Ok(ShareName.Create("next-wave"));

        ShareErrors.NameRequired(name).Description.ShouldBe("A new share needs a name: mysync sync --name next-wave");
        ShareErrors.NameRequired(null).Description.ShouldBe("A new share needs a name: mysync sync --name <name>");
        ShareErrors.NameRequired(null).Type.ShouldBe(ErrorType.Validation);
        ShareErrors.InvalidName("x").Type.ShouldBe(ErrorType.Validation);
        ShareErrors.NameTaken(name, Share()).Type.ShouldBe(ErrorType.Conflict);
        ShareErrors.NameTaken(name, Share()).Description.ShouldContain(ShareIdText);
        ShareErrors.NameNotFound(name).Type.ShouldBe(ErrorType.NotFound);
        ShareErrors.Incomplete(name, Share()).Type.ShouldBe(ErrorType.NotFound);
        ShareErrors.Incomplete(name, Share()).Description.ShouldContain("mysync delete next-wave --yes");
    }
}
