using System.Globalization;
using System.Text;

namespace MySync.Tui.Rendering;

/// <summary>How many colours the terminal shows (CLAUDE.md §10d).</summary>
public enum ColorDepth
{
    /// <summary><c>NO_COLOR</c> or <c>--no-color</c>: bold and reverse video only.</summary>
    None = 0,
    Sixteen = 1,
    TwoFiftySix = 2,
    TrueColor = 3,
}

/// <summary>
/// The one (dark) theme: each <see cref="Role"/>'s exact colour in 24-bit mode, its nearest
/// xterm-256 colour in 256-colour mode, and the named fallback in 16-colour mode, where the
/// background isn't painted and highlighted backgrounds become reverse video. With no colour,
/// only bold and reverse video remain; every status also has a symbol and a word.
/// </summary>
public static class Theme
{
    private static readonly Dictionary<Role, (int Rgb, int Ansi16)> Colors = new()
    {
        [Role.Background] = (0x161514, -1),
        [Role.Text] = (0xE7E5E4, -1),
        [Role.Muted] = (0xA8A29E, 90),
        [Role.Dim] = (0x57534E, 90),
        [Role.Rule] = (0x45423F, 90),
        [Role.SelectionBg] = (0x33312F, -2),
        [Role.Here] = (0xFDBA74, 33),
        [Role.Share] = (0x93C5FD, 94),
        [Role.Conflict] = (0xFCA5A5, 91),
        [Role.Ok] = (0x86EFAC, 92),
        [Role.KeyCapFg] = (0x161514, -1),
        [Role.KeyCapBg] = (0xA8A29E, -2),
        [Role.ChipFg] = (0x161514, -1),
        [Role.ChipBg] = (0xE7E5E4, -2),
        [Role.DialogBg] = (0x292524, -1),
        [Role.FieldBg] = (0x161514, -2),
        [Role.BarEmpty] = (0x57534E, 90),
    };

    /// <summary>The 24-bit colour of a role (for tests and documentation).</summary>
    public static int RgbOf(Role role) => Colors[role].Rgb;

    /// <summary>The SGR sequence that draws a cell in <paramref name="style"/>, starting from a reset.</summary>
    public static string Sequence(Style style, ColorDepth depth)
    {
        StringBuilder sgr = new("\u001b[0");
        if (style.Bold)
        {
            sgr.Append(";1");
        }

        switch (depth)
        {
            case ColorDepth.TrueColor:
                Append(sgr, 38, Colors[style.Fg].Rgb);
                Append(sgr, 48, Colors[style.Bg].Rgb);
                break;
            case ColorDepth.TwoFiftySix:
                sgr.Append(CultureInfo.InvariantCulture, $";38;5;{To256(Colors[style.Fg].Rgb)}");
                sgr.Append(CultureInfo.InvariantCulture, $";48;5;{To256(Colors[style.Bg].Rgb)}");
                break;
            case ColorDepth.Sixteen:
                int fg = Colors[style.Fg].Ansi16;
                if (fg > 0)
                {
                    sgr.Append(CultureInfo.InvariantCulture, $";{fg}");
                }

                if (Colors[style.Bg].Ansi16 == -2)
                {
                    sgr.Append(";7");
                }

                break;
            default:
                if (Colors[style.Bg].Ansi16 == -2)
                {
                    sgr.Append(";7");
                }

                break;
        }

        return sgr.Append('m').ToString();
    }

    private static void Append(StringBuilder sgr, int layer, int rgb) =>
        sgr.Append(CultureInfo.InvariantCulture, $";{layer};2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}");

    /// <summary>The nearest xterm-256 colour: the 6×6×6 cube or the grey ramp, whichever is closer.</summary>
    internal static int To256(int rgb)
    {
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        int[] levels = [0, 95, 135, 175, 215, 255];

        static int Nearest(int value, int[] steps)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++)
            {
                if (Math.Abs(steps[i] - value) < Math.Abs(steps[best] - value))
                {
                    best = i;
                }
            }

            return best;
        }

        int ri = Nearest(r, levels), gi = Nearest(g, levels), bi = Nearest(b, levels);
        int cube = 16 + 36 * ri + 6 * gi + bi;
        int cubeDistance = Square(levels[ri] - r) + Square(levels[gi] - g) + Square(levels[bi] - b);

        int grey = Math.Clamp((int)Math.Round(((r + g + b) / 3.0 - 8) / 10.0), 0, 23);
        int greyValue = 8 + 10 * grey;
        int greyDistance = Square(greyValue - r) + Square(greyValue - g) + Square(greyValue - b);

        return greyDistance < cubeDistance ? 232 + grey : cube;
    }

    private static int Square(int value) => value * value;
}
