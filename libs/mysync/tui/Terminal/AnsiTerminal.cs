using System.Globalization;
using System.Text;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;

namespace MySync.Tui.Terminal;

/// <summary>
/// The terminal on raw ANSI escape sequences (CLAUDE.md §10d), no package: the alternate screen
/// buffer, the cursor hidden except in a text field, autowrap off (so the bottom-right cell never
/// scrolls the screen), and each frame drawn by diffing against the previous one, so only the
/// cells that changed are written. Keys are read without echo, Ctrl+C as a key.
/// </summary>
public sealed class AnsiTerminal(IConsole console, ColorDepth colors) : ITerminal
{
    private const string Escape = "\u001b[";

    private Frame? _previous;
    private bool _entered;
    private bool _savedCtrlC;

    public (int Width, int Height) Size => (console.Width, console.Height);

    public ColorDepth Colors => colors;

    public void Enter()
    {
        if (_entered)
        {
            return;
        }

        console.EnableVirtualTerminal();
        _savedCtrlC = console.TreatControlCAsInput;
        console.TreatControlCAsInput = true;
        console.Out.Write($"{Escape}?1049h{Escape}?7l{Escape}?25l{Escape}0m{Escape}2J");
        console.Out.Flush();
        _previous = null;
        _entered = true;
    }

    public void Leave()
    {
        if (!_entered)
        {
            return;
        }

        _entered = false;
        console.Out.Write($"{Escape}0m{Escape}?7h{Escape}?25h{Escape}?1049l");
        console.Out.Flush();
        console.TreatControlCAsInput = _savedCtrlC;
        console.RestoreMode();
    }

    public void Draw(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        StringBuilder output = new();
        Frame? previous = _previous;
        if (previous is null || previous.Width != frame.Width || previous.Height != frame.Height)
        {
            output.Append(Escape).Append("0m").Append(Escape).Append("2J");
            previous = null;
        }

        Style? style = null;
        int cursorX = -1, cursorY = -1;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Cell cell = frame[x, y];
                if (previous is not null && previous[x, y] == cell)
                {
                    continue;
                }

                if (x != cursorX || y != cursorY)
                {
                    output.Append(CultureInfo.InvariantCulture, $"{Escape}{y + 1};{x + 1}H");
                }

                if (style != cell.Style)
                {
                    output.Append(Theme.Sequence(cell.Style, colors));
                    style = cell.Style;
                }

                output.Append(cell.Rune.ToString());
                cursorX = x + 1;
                cursorY = y;
            }
        }

        output.Append(frame.Cursor is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"{Escape}0m{Escape}{at.Y + 1};{at.X + 1}H{Escape}?25h")
            : $"{Escape}?25l");
        console.Out.Write(output.ToString());
        console.Out.Flush();
        _previous = frame;
    }

    public async Task<KeyInput?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (!console.KeyAvailable)
        {
            await Task.Delay(15, cancellationToken);
        }

        return Map(console.ReadKey());
    }

    /// <summary>A console key as the interface's key; Ctrl+C is its own key.</summary>
    public static KeyInput Map(ConsoleKeyInfo key)
    {
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.KeyChar == '\u0003')
        {
            return KeyInput.Of(KeyCode.CtrlC);
        }

        return key.Key switch
        {
            ConsoleKey.UpArrow => KeyInput.Of(KeyCode.Up),
            ConsoleKey.DownArrow => KeyInput.Of(KeyCode.Down),
            ConsoleKey.LeftArrow => KeyInput.Of(KeyCode.Left),
            ConsoleKey.RightArrow => KeyInput.Of(KeyCode.Right),
            ConsoleKey.PageUp => KeyInput.Of(KeyCode.PageUp),
            ConsoleKey.PageDown => KeyInput.Of(KeyCode.PageDown),
            ConsoleKey.Home => KeyInput.Of(KeyCode.Home),
            ConsoleKey.End => KeyInput.Of(KeyCode.End),
            ConsoleKey.Enter => KeyInput.Of(KeyCode.Enter),
            ConsoleKey.Escape => KeyInput.Of(KeyCode.Escape),
            ConsoleKey.Tab => KeyInput.Of(KeyCode.Tab, shift),
            ConsoleKey.Backspace => KeyInput.Of(KeyCode.Backspace),
            ConsoleKey.Delete => KeyInput.Of(KeyCode.Delete),
            _ => key.KeyChar switch
            {
                '\r' or '\n' => KeyInput.Of(KeyCode.Enter),
                '\t' => KeyInput.Of(KeyCode.Tab, shift),
                '\u001b' => KeyInput.Of(KeyCode.Escape),
                '\u007f' or '\b' => KeyInput.Of(KeyCode.Backspace),
                _ => KeyInput.Of(key.KeyChar),
            },
        };
    }
}

/// <summary>What the terminal can show, from the environment (CLAUDE.md §10d).</summary>
public static class TerminalDetection
{
    /// <summary><c>NO_COLOR</c> or <c>--no-color</c> → none; <c>COLORTERM</c> truecolor, Windows Terminal, or a Windows console with VT → 24-bit; a <c>256color</c> <c>TERM</c> → 256; else 16.</summary>
    public static ColorDepth Colors(Func<string, string?> environment, bool noColor, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (noColor || !string.IsNullOrEmpty(environment("NO_COLOR")))
        {
            return ColorDepth.None;
        }

        string colorTerm = environment("COLORTERM") ?? string.Empty;
        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(environment("WT_SESSION")))
        {
            return ColorDepth.TrueColor;
        }

        string term = environment("TERM") ?? string.Empty;
        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.TwoFiftySix;
        }

        return isWindows ? ColorDepth.TrueColor : ColorDepth.Sixteen;
    }

    /// <summary>The Unicode symbols only when the output is UTF-8.</summary>
    public static bool Unicode(Encoding outputEncoding)
    {
        ArgumentNullException.ThrowIfNull(outputEncoding);
        return outputEncoding.CodePage == Encoding.UTF8.CodePage;
    }
}
