using System.Text;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.Terminal;
using Shouldly;
using Xunit;

namespace MySync.Tui.UnitTests;

/// <summary><c>AnsiTerminal</c>'s bytes, against a buffer: the alternate screen, diffed drawing, colour depths, the cursor, keys (CLAUDE.md §10d).</summary>
public class AnsiTerminalTests
{
    private const string Esc = "\u001b[";

    private sealed class BufferConsole : IConsole
    {
        public StringWriter Buffer { get; } = new();

        public TextWriter Out => Buffer;

        public int Width { get; set; } = 20;

        public int Height { get; set; } = 3;

        public bool KeyAvailable => Keys.Count > 0;

        public Queue<ConsoleKeyInfo> Keys { get; } = new();

        public bool TreatControlCAsInput { get; set; }

        public int Restores { get; private set; }

        public ConsoleKeyInfo ReadKey() => Keys.Dequeue();

        public bool EnableVirtualTerminal() => true;

        public void RestoreMode() => Restores++;

        public string Take()
        {
            string text = Buffer.ToString();
            Buffer.GetStringBuilder().Clear();
            return text;
        }
    }

    [Fact]
    public void EnterAndLeave_Should_SwitchScreens_AndPutEverythingBack()
    {
        BufferConsole console = new() { TreatControlCAsInput = false };
        AnsiTerminal terminal = new(console, ColorDepth.TrueColor);

        terminal.Enter();
        string entered = console.Take();
        console.TreatControlCAsInput.ShouldBeTrue();
        terminal.Leave();
        string left = console.Take();
        terminal.Leave();

        entered.ShouldStartWith($"{Esc}?1049h");
        entered.ShouldContain($"{Esc}?25l");
        entered.ShouldContain($"{Esc}?7l");
        left.ShouldBe($"{Esc}0m{Esc}?7h{Esc}?25h{Esc}?1049l");
        console.TreatControlCAsInput.ShouldBeFalse();
        console.Restores.ShouldBe(1);
        console.Take().ShouldBeEmpty(); // a second Leave writes nothing
    }

    [Fact]
    public void ASecondFrame_Should_WriteOnlyTheCellsThatChanged()
    {
        BufferConsole console = new();
        AnsiTerminal terminal = new(console, ColorDepth.None);
        Frame first = new(20, 3);
        first.Put(0, 0, "hello world", new Style(Role.Text));
        Frame second = new(20, 3);
        second.Put(0, 0, "hello there", new Style(Role.Text));

        terminal.Draw(first);
        string full = console.Take();
        terminal.Draw(second);
        string changed = console.Take();

        full.ShouldContain($"{Esc}2J");
        full.ShouldContain("hello world");
        changed.ShouldNotContain("hello");
        changed.ShouldNotContain($"{Esc}2J");
        changed.ShouldStartWith($"{Esc}1;7H");
        changed.ShouldContain("there");
        changed.ShouldNotContain("world");
    }

    [Fact]
    public void AResizedFrame_Should_BeDrawnInFull()
    {
        BufferConsole console = new();
        AnsiTerminal terminal = new(console, ColorDepth.None);
        terminal.Draw(new Frame(20, 3));
        console.Take();

        terminal.Draw(new Frame(30, 4));

        console.Take().ShouldContain($"{Esc}2J");
    }

    [Theory]
    [InlineData(ColorDepth.TrueColor, "\u001b[0;1;38;2;253;186;116;48;2;22;21;20m")]
    [InlineData(ColorDepth.TwoFiftySix, "\u001b[0;1;38;5;")]
    [InlineData(ColorDepth.Sixteen, "\u001b[0;1;33m")]
    [InlineData(ColorDepth.None, "\u001b[0;1m")]
    public void Colours_Should_FollowTheDepth(ColorDepth depth, string expected)
    {
        Theme.Sequence(new Style(Role.Here, Role.Background, Bold: true), depth).ShouldStartWith(expected);
    }

    [Theory]
    [InlineData(ColorDepth.Sixteen)]
    [InlineData(ColorDepth.None)]
    public void HighlightedBackgrounds_Should_BeReverseVideoWithoutFullColour(ColorDepth depth)
    {
        Theme.Sequence(new Style(Role.Text, Role.SelectionBg), depth).ShouldContain(";7");
        Theme.Sequence(new Style(Role.KeyCapFg, Role.KeyCapBg, Bold: true), depth).ShouldContain(";7");
        Theme.Sequence(new Style(Role.Text, Role.Background), depth).ShouldNotContain(";7");
    }

    [Fact]
    public void TheCursor_Should_ShowOnlyForATextField()
    {
        BufferConsole console = new();
        AnsiTerminal terminal = new(console, ColorDepth.None);
        Frame plain = new(20, 3);
        Frame field = new(20, 3) { Cursor = (4, 2) };

        terminal.Draw(plain);
        console.Take().ShouldEndWith($"{Esc}?25l");
        terminal.Draw(field);
        console.Take().ShouldEndWith($"{Esc}3;5H{Esc}?25h");
    }

    [Theory]
    [InlineData(ConsoleKey.UpArrow, '\0', false, KeyCode.Up)]
    [InlineData(ConsoleKey.Enter, '\r', false, KeyCode.Enter)]
    [InlineData(ConsoleKey.Escape, '\u001b', false, KeyCode.Escape)]
    [InlineData(ConsoleKey.Tab, '\t', true, KeyCode.Tab)]
    [InlineData(ConsoleKey.Backspace, '\b', false, KeyCode.Backspace)]
    [InlineData(ConsoleKey.C, '\u0003', false, KeyCode.CtrlC)]
    public void Keys_Should_MapToTheInterfacesKeys(ConsoleKey key, char c, bool shift, KeyCode expected)
    {
        KeyInput mapped = AnsiTerminal.Map(new ConsoleKeyInfo(c, key, shift, alt: false, control: key == ConsoleKey.C));

        mapped.Code.ShouldBe(expected);
        AnsiTerminal.Map(new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false)).ShouldBe(KeyInput.Of('s'));
    }

    [Fact]
    public async Task ReadKey_Should_WaitForAKey()
    {
        BufferConsole console = new();
        AnsiTerminal terminal = new(console, ColorDepth.None);
        Task<KeyInput?> reading = terminal.ReadKeyAsync(TestContext.Current.CancellationToken);
        console.Keys.Enqueue(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));

        (await reading).ShouldBe(KeyInput.Of('q'));
    }

    [Theory]
    [InlineData(null, null, null, null, false, false, ColorDepth.Sixteen)]
    [InlineData("1", "truecolor", null, null, false, false, ColorDepth.None)]
    [InlineData(null, "truecolor", null, null, true, false, ColorDepth.None)]
    [InlineData(null, "truecolor", null, null, false, false, ColorDepth.TrueColor)]
    [InlineData(null, null, "{guid}", null, false, false, ColorDepth.TrueColor)]
    [InlineData(null, null, null, "xterm-256color", false, false, ColorDepth.TwoFiftySix)]
    [InlineData(null, null, null, null, false, true, ColorDepth.TrueColor)]
    public void ColourDepth_Should_ComeFromTheEnvironment(string? noColor, string? colorTerm, string? wtSession, string? term, bool flag, bool windows, ColorDepth expected)
    {
        Dictionary<string, string?> environment = new() { ["NO_COLOR"] = noColor, ["COLORTERM"] = colorTerm, ["WT_SESSION"] = wtSession, ["TERM"] = term };

        TerminalDetection.Colors(name => environment.GetValueOrDefault(name), flag, windows).ShouldBe(expected);
    }

    [Fact]
    public void UnicodeSymbols_Should_NeedUtf8Output()
    {
        TerminalDetection.Unicode(Encoding.UTF8).ShouldBeTrue();
        TerminalDetection.Unicode(Encoding.ASCII).ShouldBeFalse();
        TerminalDetection.Unicode(Encoding.Latin1).ShouldBeFalse();
    }
}
