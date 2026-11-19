using MySync.Cli.Rendering;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>A synced file can't drive the terminal through <c>mysync diff</c>.</summary>
public sealed class TerminalTextTests
{
    [Fact]
    public void Escape_Should_BecomeAVisibleSymbol()
    {
        TerminalText.Visible("x \u001b[31mred\u001b[0m").ShouldBe("x ␛[31mred␛[0m");
    }

    [Fact]
    public void OtherControlCharacters_Should_BeVisible_TabsKept()
    {
        TerminalText.Visible("a\u0007b\u0000c\u007fd\u009be\tf").ShouldBe("a␇b␀c␡d\\u009Be\tf");
    }

    [Fact]
    public void ATrailingCarriageReturn_Should_BeDropped_ButNotOneInside()
    {
        TerminalText.Visible("crlf line\r").ShouldBe("crlf line");
        TerminalText.Visible("a\rb\r").ShouldBe("a␍b");
    }

    [Fact]
    public void PlainText_Should_BeUnchanged()
    {
        const string text = "north,q3,[red]1200[/] — café";

        TerminalText.Visible(text).ShouldBeSameAs(text);
    }
}
