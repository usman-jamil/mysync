using MySync.Domain.Paths;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Paths;

public class PathRulesTests
{
    [Fact]
    public void ToNfc_Should_ComposeDecomposedCharacters()
    {
        string decomposed = "docs/café.md";

        PathRules.ToNfc(decomposed).ShouldBe("docs/café.md");
    }

    [Fact]
    public void ToNfc_Should_ReturnTheSameInstance_WhenAlreadyNormalized()
    {
        string composed = "docs/café.md";

        PathRules.ToNfc(composed).ShouldBeSameAs(composed);
    }

    [Theory]
    [InlineData("docs/readme.md")]
    [InlineData("console.txt")] // starts with "con" but isn't "con"
    [InlineData("COM10")] // only COM1-9 are reserved
    [InlineData("LPT0")]
    [InlineData("auxiliary/x")]
    [InlineData(".gitignore")]
    public void CheckWindowsCompatible_Should_AcceptNormalNames(string path)
    {
        PathRules.CheckWindowsCompatible(Path(path)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("a<b", "'<'")]
    [InlineData("a>b", "'>'")]
    [InlineData("x/a:b", "':'")] // "a:b" alone would be a drive-relative path, which SharePath rejects
    [InlineData("a\"b", "'\"'")]
    [InlineData("a|b", "'|'")]
    [InlineData("what?", "'?'")]
    [InlineData("*.log", "'*'")]
    [InlineData("name.", "end with a dot or a space")]
    [InlineData("name ", "end with a dot or a space")]
    [InlineData("CON", "reserved device name")]
    [InlineData("con", "reserved device name")]
    [InlineData("nul.txt", "reserved device name")] // an extension doesn't help
    [InlineData("Aux.tar.gz", "reserved device name")]
    [InlineData("com1", "reserved device name")]
    [InlineData("LPT9.log", "reserved device name")]
    [InlineData("ok/PRN/file.txt", "reserved device name")] // any segment, not just the last
    public void CheckWindowsCompatible_Should_ExplainWhyANameCantExistOnWindows(string path, string reason)
    {
        SharedKernel.Error error = PathRules.CheckWindowsCompatible(Path(path)).Error;

        error.Code.ShouldBe("Paths.WindowsInvalidName");
        error.Description.ShouldContain(reason);
    }

    [Fact]
    public void FindCaseCollisions_Should_FindFilesThatDifferOnlyInCase()
    {
        IReadOnlyList<CaseCollision> collisions = PathRules.FindCaseCollisions(
            [Path("Foo.txt"), Path("foo.txt"), Path("bar.txt")]);

        collisions.Count.ShouldBe(1);
        collisions[0].Spellings.ShouldBe(["Foo.txt", "foo.txt"]);
    }

    [Fact]
    public void FindCaseCollisions_Should_FindDirectoriesThatDifferOnlyInCase_EvenWhenTheFilesDiffer()
    {
        // On Windows both files would land in one folder; the scan would then see churn.
        IReadOnlyList<CaseCollision> collisions = PathRules.FindCaseCollisions(
            [Path("Docs/a.txt"), Path("docs/b.txt")]);

        collisions.Count.ShouldBe(1);
        collisions[0].Spellings.ShouldBe(["Docs", "docs"]);
    }

    [Fact]
    public void FindCaseCollisions_Should_ReportOnlyTheShallowestLevel()
    {
        // "Docs" vs "docs" already covers "Docs/a.txt" vs "docs/A.txt" below it.
        IReadOnlyList<CaseCollision> collisions = PathRules.FindCaseCollisions(
            [Path("Docs/a.txt"), Path("docs/A.txt")]);

        collisions.Count.ShouldBe(1);
        collisions[0].Spellings.ShouldBe(["Docs", "docs"]);
    }

    [Fact]
    public void FindCaseCollisions_Should_GroupThreeOrMoreSpellings()
    {
        PathRules.FindCaseCollisions([Path("README"), Path("readme"), Path("ReadMe")])
            .Single().Spellings.ShouldBe(["README", "ReadMe", "readme"]);
    }

    [Fact]
    public void FindCaseCollisions_Should_ReturnNothing_ForDistinctOrRepeatedPaths()
    {
        PathRules.FindCaseCollisions([Path("a/b"), Path("a/b"), Path("a/c"), Path("ab")]).ShouldBeEmpty();
    }

    [Fact]
    public void FindCaseCollisions_Should_NotTreatSharpSAndDoubleSAsTheSame_LikeNtfs()
    {
        PathRules.FindCaseCollisions([Path("Straße"), Path("STRASSE")]).ShouldBeEmpty();
    }

    [Fact]
    public void FindCaseCollisions_Should_FoldNonAsciiLetters()
    {
        PathRules.FindCaseCollisions([Path("Été"), Path("été")]).Count.ShouldBe(1);
    }

    [Fact]
    public void CaseCollision_Affects_Should_CoverTheSpellingsAndEverythingBelowThem()
    {
        CaseCollision collision = PathRules.FindCaseCollisions([Path("Docs/a.txt"), Path("docs/b.txt")]).Single();

        collision.Affects(Path("Docs/a.txt")).ShouldBeTrue();
        collision.Affects(Path("docs/b.txt")).ShouldBeTrue();
        collision.Affects(Path("docs")).ShouldBeTrue();
        collision.Affects(Path("docs2/c.txt")).ShouldBeFalse();
        collision.Affects(Path("other.txt")).ShouldBeFalse();
    }
}
