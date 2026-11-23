using System.Text;

namespace MySync.Tui.Rendering;

/// <summary>A colour role (CLAUDE.md §10d's theme table). Views pick roles; <see cref="Theme"/> turns them into colours.</summary>
public enum Role
{
    Background = 0,
    Text = 1,
    Muted = 2,
    Dim = 3,
    Rule = 4,
    SelectionBg = 5,
    Here = 6,
    Share = 7,
    Conflict = 8,
    Ok = 9,
    KeyCapFg = 10,
    KeyCapBg = 11,
    ChipFg = 12,
    ChipBg = 13,
    DialogBg = 14,
    FieldBg = 15,
    BarEmpty = 16,
}

/// <summary>How one cell is drawn.</summary>
public readonly record struct Style(Role Fg = Role.Text, Role Bg = Role.Background, bool Bold = false)
{
    public static Style Default { get; } = new(Role.Text, Role.Background, Bold: false);

    public Style WithBg(Role bg) => this with { Bg = bg };

    public Style Bolded() => this with { Bold = true };
}

/// <summary>One character cell. Every rune takes one cell (wide characters are not measured).</summary>
public readonly record struct Cell(Rune Rune, Style Style)
{
    public static Cell Blank { get; } = new(new Rune(' '), Style.Default);
}

/// <summary>
/// What a view returns: a grid of cells, <see cref="Width"/> × <see cref="Height"/>. Views write
/// into it; the runtime draws it (only the cells that changed). Writing outside is clipped.
/// </summary>
public sealed class Frame
{
    private readonly Cell[] _cells;

    public Frame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        _cells = new Cell[width * height];
        Array.Fill(_cells, Cell.Blank);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Where the text cursor is shown (a text field); <c>null</c> hides it.</summary>
    public (int X, int Y)? Cursor { get; set; }

    public Cell this[int x, int y]
    {
        get => _cells[y * Width + x];
        set
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                _cells[y * Width + x] = value;
            }
        }
    }

    /// <summary>Writes <paramref name="text"/> from (<paramref name="x"/>, <paramref name="y"/>), at most <paramref name="max"/> cells.</summary>
    /// <returns>The cells written.</returns>
    public int Put(int x, int y, string text, Style style, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (y < 0 || y >= Height)
        {
            return 0;
        }

        int written = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (written >= max || x + written >= Width)
            {
                break;
            }

            this[x + written, y] = new Cell(Rune.IsControl(rune) ? new Rune('?') : rune, style);
            written++;
        }

        return written;
    }

    /// <summary>Paints a rectangle with spaces in <paramref name="style"/>.</summary>
    public void Fill(int x, int y, int width, int height, Style style)
    {
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            for (int column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            {
                this[column, row] = new Cell(new Rune(' '), style);
            }
        }
    }

    /// <summary>Changes the background of a rectangle, keeping its text.</summary>
    public void Shade(int x, int y, int width, int height, Role bg)
    {
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            for (int column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            {
                Cell cell = this[column, row];
                this[column, row] = cell with { Style = cell.Style.WithBg(bg) };
            }
        }
    }

    /// <summary>Everything redrawn in <see cref="Role.Dim"/>, with no colour and no bold: what sits behind a dialog.</summary>
    public void Dim()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = _cells[i] with { Style = new Style(Role.Dim) };
        }
    }

    /// <summary>The text, one line per row, trailing spaces trimmed.</summary>
    public string ToText()
    {
        StringBuilder text = new();
        for (int y = 0; y < Height; y++)
        {
            StringBuilder row = new();
            for (int x = 0; x < Width; x++)
            {
                row.Append(this[x, y].Rune.ToString());
            }

            text.Append(row.ToString().TrimEnd()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The styles, one letter per cell (see <see cref="Code"/>), foreground and background grids
    /// side by side, for snapshot tests. A bold foreground is upper case.
    /// </summary>
    public string ToRoles()
    {
        StringBuilder text = new();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Style style = this[x, y].Style;
                char code = Code(style.Fg);
                text.Append(style.Bold ? char.ToUpperInvariant(code) : code);
            }

            text.Append(" | ");
            for (int x = 0; x < Width; x++)
            {
                text.Append(Code(this[x, y].Style.Bg));
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>One letter per role: background <c>.</c>, text <c>t</c>, muted <c>m</c>, dim <c>d</c>, rule <c>r</c>, …</summary>
    public static char Code(Role role) => role switch
    {
        Role.Background => '.',
        Role.Text => 't',
        Role.Muted => 'm',
        Role.Dim => 'd',
        Role.Rule => 'r',
        Role.SelectionBg => 's',
        Role.Here => 'h',
        Role.Share => 'b',
        Role.Conflict => 'c',
        Role.Ok => 'o',
        Role.KeyCapFg => 'k',
        Role.KeyCapBg => 'K',
        Role.ChipFg => 'p',
        Role.ChipBg => 'P',
        Role.DialogBg => 'g',
        Role.FieldBg => 'f',
        _ => 'e',
    };
}
