using MySync.Domain.Sync;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Sync;

public class ConflictKindCodesTests
{
    [Theory]
    [InlineData(ConflictKind.ModifyModify, "modify/modify")]
    [InlineData(ConflictKind.AddAdd, "add/add")]
    [InlineData(ConflictKind.ModifyDelete, "modify/delete")]
    [InlineData(ConflictKind.DeleteModify, "delete/modify")]
    [InlineData(ConflictKind.Type, "type")]
    public void Codes_Should_MatchTheDocumentedStrings_AndRoundTrip(ConflictKind kind, string code)
    {
        kind.ToCode().ShouldBe(code);
        ConflictKindCodes.TryParse(code, out ConflictKind parsed).ShouldBeTrue();
        parsed.ShouldBe(kind);
    }

    [Theory]
    [InlineData("Modify/Modify")]
    [InlineData("conflict")]
    [InlineData(null)]
    public void TryParse_Should_RejectUnknownCodes(string? code)
    {
        ConflictKindCodes.TryParse(code, out _).ShouldBeFalse();
    }
}
