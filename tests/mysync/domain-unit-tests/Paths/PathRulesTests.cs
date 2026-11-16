using MySync.Domain.Paths;
using Shouldly;
using Xunit;

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
}
