using MySync.Domain.Manifests;
using MySync.Domain.UnitTests.TestSupport;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Manifests;

public class RevisionTests
{
    [Fact]
    public void Initial_Should_BeOne()
    {
        Revision.Initial.Value.ShouldBe(1);
    }

    [Fact]
    public void Next_Should_AddExactlyOne()
    {
        Fixtures.Rev(17).Next().ShouldBe(Fixtures.Rev(18));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryCreate_Should_RejectValuesBelowOne(long value)
    {
        Revision.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Next_Should_ThrowRatherThanWrap_AtTheMaximum()
    {
        Should.Throw<OverflowException>(() => Fixtures.Rev(long.MaxValue).Next());
    }

    [Fact]
    public void Comparison_Should_FollowTheNumericValue()
    {
        (Fixtures.Rev(9) < Fixtures.Rev(10)).ShouldBeTrue();
        (Fixtures.Rev(10) >= Fixtures.Rev(10)).ShouldBeTrue();
        Fixtures.Rev(2).CompareTo(null).ShouldBePositive();
    }
}
