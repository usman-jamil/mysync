using System.Security.Cryptography;
using System.Text;
using MySync.Application.Abstractions.Hashing;
using MySync.Infrastructure.Hashing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Hashing;

public class Sha256ContentHasherTests
{
    private readonly Sha256ContentHasher _hasher = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task Hash_Should_MatchTheStandardTestVectors(string content, string expected)
    {
        (await _hasher.HashAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), Ct)).Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Hash_Should_StreamContentLargerThanItsBuffer_AndMatchIncremental()
    {
        byte[] content = RandomNumberGenerator.GetBytes(Sha256ContentHasher.BufferSize * 3 + 17);

        Domain.Content.ContentHash streamed = await _hasher.HashAsync(new MemoryStream(content), Ct);

        using IIncrementalContentHash incremental = _hasher.CreateIncremental();
        foreach (byte[] chunk in content.Chunk(1000))
        {
            incremental.Append(chunk);
        }

        streamed.Value.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
        incremental.Finish().ShouldBe(streamed);
    }
}
