using System.Text;
using MySync.Domain.Diffing;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Diffing;

public sealed class UnifiedDiffTests
{
    private static readonly string[] Ten = [.. Enumerable.Range(1, 10).Select(i => "l" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void IdenticalInputs_Should_HaveNoHunks_AndNoPatch()
    {
        UnifiedDiff diff = Diff("a\nb\n", "a\nb\n");

        diff.IsIdentical.ShouldBeTrue();
        diff.ToPatchText("x", "y").ShouldBeEmpty();
    }

    [Fact]
    public void OneChange_Should_BeOneHunk_WithThreeLinesOfContext()
    {
        string[] changed = [.. Ten];
        changed[4] = "five";

        var diff = UnifiedDiff.Create(Ten, true, changed, true);

        diff.ToPatchText("local/f", "remote/f").ShouldBe(
            "--- local/f\n+++ remote/f\n@@ -2,7 +2,7 @@\n l2\n l3\n l4\n-l5\n+five\n l6\n l7\n l8\n");
        diff.Added.ShouldBe(1);
        diff.Removed.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, "@@ -5 +5 @@")]
    [InlineData(1, "@@ -4,3 +4,3 @@")]
    [InlineData(3, "@@ -2,7 +2,7 @@")]
    public void Context_Should_SetTheHunkSize(int context, string header)
    {
        string[] changed = [.. Ten];
        changed[4] = "five";

        UnifiedDiff.Create(Ten, true, changed, true, context).Hunks.Single().Header.ShouldBe(header);
    }

    [Theory]
    [InlineData(3, 1)] // changes at lines 2 and 8: 5 lines apart, within 2·3, so one hunk
    [InlineData(2, 2)] // 5 > 2·2: two hunks
    [InlineData(0, 2)]
    public void NearbyChanges_Should_ShareAHunk_WhenTheirContextsTouch(int context, int hunks)
    {
        string[] changed = [.. Ten];
        changed[1] = "two";
        changed[7] = "eight";

        UnifiedDiff.Create(Ten, true, changed, true, context).Hunks.Length.ShouldBe(hunks);
    }

    [Fact]
    public void AdjacentChanges_Should_MergeAtContextZero()
    {
        string[] changed = [.. Ten];
        changed[1] = "two";
        changed[2] = "three";

        UnifiedDiff.Create(Ten, true, changed, true, 0).Hunks.Single().Header.ShouldBe("@@ -2,2 +2,2 @@");
    }

    [Fact]
    public void ChangesAtTheVeryStartAndEnd_Should_ClampTheirContext()
    {
        string[] changed = [.. Ten];
        changed[0] = "one";
        changed[9] = "ten";

        var diff = UnifiedDiff.Create(Ten, true, changed, true);

        diff.Hunks.Select(hunk => hunk.Header).ShouldBe(["@@ -1,4 +1,4 @@", "@@ -7,4 +7,4 @@"]);
    }

    [Fact]
    public void AnInsertion_WithNoContext_Should_StartAtTheLineBefore()
    {
        List<string> changed = [.. Ten];
        changed.Insert(5, "new");

        UnifiedDiff.Create(Ten, true, changed, true, 0).Hunks.Single().Header.ShouldBe("@@ -5,0 +6 @@");
    }

    [Fact]
    public void AFullyAddedFile_Should_StartAtZero()
    {
        UnifiedDiff diff = Diff(string.Empty, "a\nb\n");

        diff.ToPatchText("o", "n").ShouldBe("--- o\n+++ n\n@@ -0,0 +1,2 @@\n+a\n+b\n");
    }

    [Fact]
    public void AFullyDeletedFile_Should_EndAtZero()
    {
        Diff("a\nb\nc\n", string.Empty).Hunks.Single().Header.ShouldBe("@@ -1,3 +0,0 @@");
    }

    [Fact]
    public void AMissingFinalNewline_Should_BeMarked_AsDiffDoes()
    {
        Diff("a\nb", "a\nc").ToPatchText("o", "n").ShouldBe(
            "--- o\n+++ n\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n");
    }

    [Fact]
    public void AddingOnlyTheFinalNewline_Should_ChangeTheLastLine()
    {
        UnifiedDiff diff = Diff("a\nb", "a\nb\n");

        diff.ToPatchText("o", "n").ShouldBe("--- o\n+++ n\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n");
        diff.Hunks.Single().OldNoNewlineAtEnd.ShouldBeTrue();
        diff.Hunks.Single().NewNoNewlineAtEnd.ShouldBeFalse();
    }

    [Fact]
    public void AnUnchangedLastLineWithoutNewline_Should_BeMarkedOnce_AsContext()
    {
        Diff("x\na", "y\na").ToPatchText("o", "n").ShouldBe(
            "--- o\n+++ n\n@@ -1,2 +1,2 @@\n-x\n+y\n a\n\\ No newline at end of file\n");
    }

    [Fact]
    public void CrLfLines_Should_KeepTheirCarriageReturn_InThePatch()
    {
        Diff("a\r\nb\r\n", "a\r\nc\r\n").ToPatchText("o", "n").ShouldBe("--- o\n+++ n\n@@ -1,2 +1,2 @@\n a\r\n-b\r\n+c\r\n");
    }

    [Fact]
    public void Context_Should_BeLimited()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => UnifiedDiff.Create(Ten, true, Ten, true, DiffLimits.MaxContext + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => UnifiedDiff.Create(Ten, true, Ten, true, -1));
    }

    private static UnifiedDiff Diff(string oldText, string newText) =>
        UnifiedDiff.Create(TextContent.From(Encoding.UTF8.GetBytes(oldText)), TextContent.From(Encoding.UTF8.GetBytes(newText)));
}
