using MySync.Domain.Paths;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Paths;

public class SharePathTests
{
    [Theory]
    [InlineData("readme.md")]
    [InlineData("docs/readme.md")]
    [InlineData("a/b/c/d/e.txt")]
    [InlineData("..hidden")] // dots inside a name are fine
    [InlineData("name..")]
    [InlineData("...")]
    [InlineData(".config/app")]
    [InlineData("with space/and-dash_und.ext")]
    [InlineData("a/C:")] // a colon is only a drive letter at the very start (Windows rules are separate)
    [InlineData("日本語/한글.txt")]
    [InlineData("emoji/\uD83D\uDE00.txt")] // a correctly paired surrogate
    public void Create_Should_AcceptValidRelativePaths(string input)
    {
        SharePath.Create(input).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Create_Should_NormalizeToNfc()
    {
        Path("docs/cafe\u0301.md").Value.ShouldBe("docs/caf\u00E9.md");
    }

    [Fact]
    public void Create_Should_MakeNfdAndNfcSpellingsEqual()
    {
        Path("cafe\u0301").ShouldBe(Path("caf\u00E9"));
    }

    [Theory]
    [InlineData("", "Paths.Empty")]
    [InlineData("/etc/passwd", "Paths.Absolute")]
    [InlineData("/", "Paths.Absolute")]
    [InlineData("C:/Windows", "Paths.DriveLetter")]
    [InlineData("c:", "Paths.DriveLetter")]
    [InlineData("a\\b", "Paths.Backslash")]
    [InlineData("..\\..\\x", "Paths.Backslash")]
    [InlineData("\\\\server\\share", "Paths.Backslash")]
    [InlineData("a/", "Paths.EmptySegment")]
    [InlineData("a//b", "Paths.EmptySegment")]
    [InlineData("..", "Paths.RelativeSegment")]
    [InlineData(".", "Paths.RelativeSegment")]
    [InlineData("../outside", "Paths.RelativeSegment")]
    [InlineData("a/../../outside", "Paths.RelativeSegment")]
    [InlineData("a/./b", "Paths.RelativeSegment")]
    [InlineData("a\0b", "Paths.ControlCharacter")]
    [InlineData("a\u0001b", "Paths.ControlCharacter")]
    [InlineData("a\nb", "Paths.ControlCharacter")]
    [InlineData("a\u001B[31mred", "Paths.ControlCharacter")] // terminal escape sequence
    [InlineData("a\u007Fb", "Paths.ControlCharacter")] // DEL
    [InlineData("a\u0085b", "Paths.ControlCharacter")] // C1 control (NEL)
    public void Create_Should_RejectUnsafePaths(string input, string expectedCode)
    {
        SharePath.Create(input).Error.Code.ShouldBe(expectedCode);
    }

    // Not [InlineData]: attribute strings are stored as UTF-8, so an unpaired surrogate would
    // arrive as U+FFFD and the test would silently check something else.
    [Fact]
    public void Create_Should_RejectUnpairedSurrogates()
    {
        string[] inputs = ["a" + '\uD800' + "b", "a" + '\uDC00', '\uDBFF' + "/x"];

        foreach (string input in inputs)
        {
            SharePath.Create(input).Error.Code.ShouldBe("Paths.UnpairedSurrogate");
        }
    }

    [Fact]
    public void Create_Should_RejectNull()
    {
        SharePath.Create(null).Error.Code.ShouldBe("Paths.Empty");
    }

    [Fact]
    public void NameAndParent_Should_SplitOnTheLastSeparator()
    {
        SharePath path = Path("docs/guides/setup.md");

        path.Name.ShouldBe("setup.md");
        path.Parent.ShouldBe(Path("docs/guides"));
        path.Parent!.Parent.ShouldBe(Path("docs"));
        path.Parent.Parent!.Parent.ShouldBeNull();
        path.Segments.ShouldBe(["docs", "guides", "setup.md"]);
    }

    [Theory]
    [InlineData("a/b", "a", true)]
    [InlineData("a/b/c", "a", true)]
    [InlineData("a", "a", false)] // not strictly below itself
    [InlineData("ab/c", "a", false)] // a common prefix is not a parent
    [InlineData("a-b/c", "a", false)]
    [InlineData("A/b", "a", false)] // ordinal
    public void IsUnder_Should_RespectSegmentBoundaries(string path, string directory, bool expected)
    {
        Path(path).IsUnder(Path(directory)).ShouldBe(expected);
    }

    [Fact]
    public void Equality_Should_BeOrdinal_SoCaseVariantsAreDifferentPaths()
    {
        Path("Foo.txt").ShouldNotBe(Path("foo.txt"));
    }

    [Fact]
    public void OrdinalComparer_Should_SortByUtf16CodeUnits()
    {
        string[] input = ["b", "a/b", "B", "a-b", "\u00E9", "z"];

        SharePath[] sorted = [.. input.Select(Path).Order(SharePath.OrdinalComparer)];

        sorted.Select(path => path.Value).ShouldBe(["B", "a-b", "a/b", "b", "z", "\u00E9"]);
    }

    [Fact]
    public void Errors_Should_ShowControlCharactersEscaped_SoACraftedNameCannotDriveTheTerminal()
    {
        string description = SharePath.Create("x\u001B[2Jy").Error.Description;

        description.ShouldNotContain("\u001B");
        description.ShouldContain("\\u001B[2J");
    }
}
