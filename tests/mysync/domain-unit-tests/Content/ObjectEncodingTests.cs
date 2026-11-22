using MySync.Domain.Content;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Content;

/// <summary>An object's <c>Content-Encoding</c> (CLAUDE.md §6): raw or gzip, never guessed.</summary>
public sealed class ObjectEncodingTests
{
    [Theory]
    [InlineData(null, ObjectEncoding.Identity)]
    [InlineData("", ObjectEncoding.Identity)]
    [InlineData("identity", ObjectEncoding.Identity)]
    [InlineData("gzip", ObjectEncoding.Gzip)]
    [InlineData("GZip", ObjectEncoding.Gzip)]
    [InlineData(" gzip ", ObjectEncoding.Gzip)]
    public void KnownEncodings_Should_Read(string? value, ObjectEncoding expected)
    {
        ObjectEncodings.FromContentEncoding(value).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("gzip, br")]
    [InlineData("x-gzip")]
    public void AnythingElse_Should_BeUnsupported_NotGuessed(string value)
    {
        SharedKernel.Error error = ObjectEncodings.FromContentEncoding(value).Error;

        error.Code.ShouldBe("Storage.UnsupportedEncoding");
        error.Description.ShouldContain("Upgrade mysync");
    }

    [Fact]
    public void AnUnsupportedValue_Should_BeShownSafely()
    {
        ObjectEncodings.FromContentEncoding("evil\u001b[31m" + new string('x', 100)).Error.Description.ShouldNotContain("\u001b");
    }

    [Fact]
    public void TheHeaderValue_Should_BeGzipOrNothing()
    {
        ObjectEncoding.Gzip.ToContentEncoding().ShouldBe("gzip");
        ObjectEncoding.Identity.ToContentEncoding().ShouldBeNull();
    }
}
