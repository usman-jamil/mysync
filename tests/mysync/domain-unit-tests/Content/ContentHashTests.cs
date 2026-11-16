using System.Security.Cryptography;
using System.Text;
using MySync.Domain.Content;
using MySync.Domain.UnitTests.TestSupport;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Content;

public class ContentHashTests
{
    [Fact]
    public void FromBytes_Should_ProduceLowercaseHexOfTheSha256()
    {
        var hash = ContentHash.FromBytes(SHA256.HashData(Encoding.ASCII.GetBytes("test")));

        hash.Value.ShouldBe(Fixtures.HashOfTest);
    }

    [Fact]
    public void FromBytes_Should_RejectAnythingButThirtyTwoBytes()
    {
        Should.Throw<ArgumentException>(() => ContentHash.FromBytes(new byte[31]));
    }

    [Fact]
    public void TryParse_Should_StoreUppercaseInputAsLowercase()
    {
        ContentHash.TryParse(Fixtures.HashOfTest.ToUpperInvariant(), out ContentHash? hash).ShouldBeTrue();

        hash.Value.ShouldBe(Fixtures.HashOfTest);
        hash.ShouldBe(Fixtures.Hash(Fixtures.HashOfTest));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")] // 63 characters
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a088")] // 65 characters
    [InlineData("zf86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")] // not hex
    [InlineData(" 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")] // whitespace
    public void TryParse_Should_RejectMalformedHashes(string input)
    {
        ContentHash.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void FanOut_Should_BeTheFirstTwoHexCharacters()
    {
        Fixtures.Hash(Fixtures.HashOfTest).FanOut.ShouldBe("9f");
    }
}
