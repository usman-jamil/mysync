using System.Diagnostics;
using MySync.Application.Session;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using MySync.Tui.Views;
using Shouldly;
using Xunit;
using static MySync.Tui.UnitTests.Fixtures;

namespace MySync.Tui.UnitTests;

/// <summary>The layout rules: column widths, cutting with an ellipsis, key hints, and drawing only what is visible (CLAUDE.md §10d).</summary>
public class LayoutTests
{
    [Theory]
    [InlineData(80, 44, 0)]
    [InlineData(99, 63, 0)]
    [InlineData(100, 40, 24)]
    [InlineData(140, 80, 24)]
    public void Columns_Should_GiveNameTheRest_AndDropDetailsBelowOneHundred(int width, int name, int details)
    {
        (int Name, int Status, int Size, int Details) columns = Screens.Columns(width);

        columns.ShouldBe((name, 22, 8, details));
    }

    [Fact]
    public void Text_Should_BeCutWithAnEllipsis()
    {
        Text.Fit("deploy/notes.md", 10, "…").ShouldBe("deploy/no…");
        Text.Fit("short", 10, "…").ShouldBe("short");
        Text.FitLeft(@"C:\Users\usman\deploy\config", 12, "…").ShouldBe(@"…ploy\config");
        Text.Fit("abc", 0, "…").ShouldBeEmpty();
        Text.Pad("ab", 4, "~").ShouldBe("ab  ");
        Text.PadLeft("ab", 4, "~").ShouldBe("  ab");
    }

    [Theory]
    [InlineData(612, "612 B")]
    [InlineData(4300, "4.2 KB")]
    [InlineData(1_363_148, "1.3 MB")]
    [InlineData(3_221_225_472, "3.0 GB")]
    public void Sizes_Should_BeShortAndBinary(long bytes, string text) => Text.Bytes(bytes).ShouldBe(text);

    [Fact]
    public void ALongNameAndPath_Should_BeCut_NeverWrapped()
    {
        string name = "config/" + new string('x', 200) + ".yml";
        TuiState state = MainScreen(status: StatusOf([Entry(name, FileStatus.NewHere)], 1)) with { Options = Options() with { FolderPath = "/very/" + new string('p', 300) } };

        string[] lines = Screens.Render(state).ToText().Split('\n');

        lines[0].ShouldEndWith(new string('p', 20));
        lines[0].ShouldContain("…");
        lines.ShouldContain(line => line.Contains("xxxx…", StringComparison.Ordinal));
        lines.ShouldAllBe(line => Text.Width(line) <= 100);
    }

    [Fact]
    public void KeyHints_Should_BeDroppedFromTheRight_ButHelpAlwaysFits()
    {
        string wide = Screens.Render(MainScreen(140, 30)).ToText().Split('\n')[29];
        string narrow = Screens.Render(MainScreen(80, 24)).ToText().Split('\n')[23];

        wide.ShouldContain("q  Quit");
        wide.ShouldContain("?  Help");
        narrow.ShouldContain("r  Rescan");
        narrow.ShouldContain("?  Help");
        narrow.ShouldNotContain("q  Quit");
    }

    [Fact]
    public void AHundredThousandEntries_Should_DrawOnlyTheVisibleRows_InTheSameTime()
    {
        TuiState small = MainScreen(status: Many(1_000));
        TuiState large = MainScreen(status: Many(100_000));
        large.Rows.Length.ShouldBe(100_000 + 100);

        static TimeSpan Draw(TuiState state)
        {
            Screens.Render(state);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                Screens.Render(state with { Selected = i % 50 });
            }

            return watch.Elapsed;
        }

        Draw(small);
        TimeSpan smallTime = Draw(small);
        TimeSpan largeTime = Draw(large);

        largeTime.ShouldBeLessThan(smallTime * 4 + TimeSpan.FromMilliseconds(50));
    }

    private static FolderStatus Many(int count) =>
        StatusOf([.. Enumerable.Range(0, count).Select(i => Entry($"folder-{i % 100:000}/file-{i:000000}.log", i % 7 == 0 ? FileStatus.ChangedHere : FileStatus.Synced)).OrderBy(entry => entry.Path, StringComparer.Ordinal)], count);
}
