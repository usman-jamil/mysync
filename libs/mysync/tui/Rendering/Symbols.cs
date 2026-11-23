namespace MySync.Tui.Rendering;

/// <summary>Every symbol the screen uses, in a Unicode set and an ASCII set (<c>--ascii</c>, or when the output isn't UTF-8).</summary>
public sealed record Symbols(
    string Up,
    string Down,
    string Delete,
    string Conflict,
    string Done,
    string Waiting,
    string Selected,
    string FolderOpen,
    string FolderClosed,
    string Rule,
    string BoxTopLeft,
    string BoxTopRight,
    string BoxBottomLeft,
    string BoxBottomRight,
    string BoxHorizontal,
    string BoxVertical,
    string BarFull,
    string BarEmpty,
    string Ellipsis,
    string Dot)
{
    public static Symbols Unicode { get; } = new("↑", "↓", "−", "!", "✓", "·", "▸", "▾", "▸", "─", "┌", "┐", "└", "┘", "─", "│", "█", "░", "…", "·");

    public static Symbols Ascii { get; } = new("^", "v", "-", "!", "=", ".", ">", "v", ">", "-", "+", "+", "+", "+", "-", "|", "#", ".", "~", "-");
}
