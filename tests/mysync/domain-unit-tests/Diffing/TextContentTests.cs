using System.Text;
using MySync.Domain.Diffing;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Diffing;

public sealed class TextContentTests
{
    [Fact]
    public void ANulByte_Should_MakeItBinary()
    {
        TextContent.From(new byte[] { (byte)'a', 0, (byte)'b' }).IsBinary.ShouldBeTrue();
    }

    [Fact]
    public void ANulByte_PastTheFirst8KiB_Should_NotMakeItBinary_ButValidUtf8StillDecides()
    {
        byte[] bytes = [.. Enumerable.Repeat((byte)'a', DiffLimits.BinarySniffBytes + 10)];
        bytes[^1] = 0;

        TextContent.From(bytes).IsBinary.ShouldBeFalse(); // NUL is valid UTF-8; only the first 8 KiB are sniffed
    }

    [Fact]
    public void InvalidUtf8_Should_MakeItBinary()
    {
        TextContent.From(new byte[] { (byte)'a', 0xC3, 0x28 }).IsBinary.ShouldBeTrue();
    }

    [Fact]
    public void TheEmptyFile_Should_BeTextWithNoLines()
    {
        var empty = TextContent.From(ReadOnlyMemory<byte>.Empty);

        empty.IsBinary.ShouldBeFalse();
        empty.Lines.ShouldBeEmpty();
        empty.EndsWithNewline.ShouldBeTrue();
        empty.LineEndings.ShouldBe(LineEndingStyle.None);
    }

    [Fact]
    public void AUtf8Bom_Should_BeRecognised_AndNotBePartOfTheText()
    {
        var text = TextContent.From(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\n' });

        text.Encoding.ShouldBe(TextEncodingKind.Utf8Bom);
        text.Lines.ShouldBe(["hi"]);
    }

    [Theory]
    [InlineData(false, TextEncodingKind.Utf16LittleEndian)]
    [InlineData(true, TextEncodingKind.Utf16BigEndian)]
    public void Utf16_WithABom_Should_BeDecoded(bool bigEndian, TextEncodingKind expected)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("héllo\nwörld\n")];

        var text = TextContent.From(bytes);

        text.IsBinary.ShouldBeFalse();
        text.Encoding.ShouldBe(expected);
        text.Lines.ShouldBe(["héllo", "wörld"]);
    }

    [Theory]
    [InlineData("a\nb\n", LineEndingStyle.Lf)]
    [InlineData("a\r\nb\r\n", LineEndingStyle.CrLf)]
    [InlineData("a\rb\r", LineEndingStyle.Cr)]
    [InlineData("a\r\nb\n", LineEndingStyle.Mixed)]
    [InlineData("a", LineEndingStyle.None)]
    public void LineEndings_Should_BeDetected(string content, LineEndingStyle expected)
    {
        TextContent.From(Encoding.UTF8.GetBytes(content)).LineEndings.ShouldBe(expected);
    }

    [Fact]
    public void Lines_Should_SplitOnLf_KeepingCr()
    {
        var text = TextContent.From(Encoding.UTF8.GetBytes("a\r\nb"));

        text.Lines.ShouldBe(["a\r", "b"]);
        text.EndsWithNewline.ShouldBeFalse();
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n", true)]
    [InlineData("a\r\nb\n", "a\nb\n", true)]
    [InlineData("a\nb\n", "a\nb\n", false)]
    [InlineData("a\r\nc\r\n", "a\nb\n", false)]
    [InlineData("a\r\nb", "a\nb\n", false)]
    public void OnlyLineEndingsDiffer_Should_IgnoreCarriageReturnsAlone(string left, string right, bool expected)
    {
        TextComparison.OnlyLineEndingsDiffer(Text(left), Text(right)).ShouldBe(expected);
    }

    [Fact]
    public void OnlyEncodingDiffers_Should_SpotABomOnlyDifference()
    {
        var withBom = TextContent.From(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' });

        TextComparison.OnlyEncodingDiffers(withBom, Text("a\n")).ShouldBeTrue();
        TextComparison.OnlyEncodingDiffers(withBom, Text("b\n")).ShouldBeFalse();
        TextComparison.OnlyEncodingDiffers(Text("a\n"), Text("a\n")).ShouldBeFalse();
    }

    private static TextContent Text(string content) => TextContent.From(Encoding.UTF8.GetBytes(content));
}
