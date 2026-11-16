using MySync.Domain.Shares;
using MySync.Domain.UnitTests.TestSupport;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Shares;

public class ShareIdTests
{
    [Fact]
    public void New_Should_ProduceALowercaseDashedUuid()
    {
        string text = ShareId.New().ToString();

        text.ShouldMatch("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
    }

    [Fact]
    public void TryParse_Should_NormalizeToLowercase_BecauseTheIdIsPartOfEveryBlobName()
    {
        ShareId.TryParse(Fixtures.ShareIdText.ToUpperInvariant(), out ShareId? id).ShouldBeTrue();

        id.ToString().ShouldBe(Fixtures.ShareIdText);
        id.ShouldBe(Fixtures.Share());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("0b6f3c1e8f0a4c439d3e5a1f2b7c9e10")] // no dashes
    [InlineData("{0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10}")] // braces
    [InlineData(" 0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10")] // whitespace
    [InlineData("0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e1")] // one digit short
    [InlineData("00000000-0000-0000-0000-000000000000")] // nil UUID
    public void TryParse_Should_RejectAnythingButTheCanonicalForm(string input)
    {
        ShareId.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_Should_RejectNull()
    {
        ShareId.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void InvalidId_Should_BeAUsageError_ThatNeverEchoesTheInput()
    {
        // InvalidId takes no argument on purpose: a pasted connection string can't leak through it.
        Error error = ShareErrors.InvalidId();

        error.Type.ShouldBe(ErrorType.Validation);
        error.Description.ShouldContain("must be a UUID");
    }
}
