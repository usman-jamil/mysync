using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace MySync.Domain.Diffing;

public enum DiffLineKind
{
    Context,
    Removed,
    Added,
}

/// <summary>
/// One line of a hunk. <see cref="Text"/> is the line without its <c>\n</c> (a <c>\r</c> before it is
/// kept, so the patch reproduces CRLF files). <see cref="NoNewlineAfter"/>: this is the last line of its
/// file and has no line break, so a patch writes <c>\ No newline at end of file</c> after it.
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, bool NoNewlineAfter = false)
{
    /// <summary>The patch prefix: ' ', '-' or '+'.</summary>
    public char Op => Kind switch
    {
        DiffLineKind.Removed => '-',
        DiffLineKind.Added => '+',
        _ => ' ',
    };
}

/// <summary>
/// A hunk, with GNU diff's header numbers: 1-based starts, and a start of the line before the hunk
/// (0 at the top) when that side's count is 0.
/// </summary>
public sealed record DiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, ImmutableArray<DiffLine> Lines)
{
    public bool OldNoNewlineAtEnd => Lines.Any(line => line.NoNewlineAfter && line.Kind != DiffLineKind.Added);

    public bool NewNoNewlineAtEnd => Lines.Any(line => line.NoNewlineAfter && line.Kind != DiffLineKind.Removed);

    /// <summary><c>@@ -a,b +c,d @@</c>, a count of 1 left out as diff does.</summary>
    public string Header => $"@@ -{Range(OldStart, OldLines)} +{Range(NewStart, NewLines)} @@";

    private static string Range(int start, int count) => count == 1
        ? start.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{start},{count}");
}

/// <summary>
/// A unified diff of two texts: hunks with <c>n</c> lines of context, overlapping or touching ones
/// merged (two changes at most 2·n lines apart share a hunk), as GNU diff does.
/// </summary>
public sealed class UnifiedDiff
{
    public const string NoNewlineMarker = "\\ No newline at end of file";

    private UnifiedDiff(ImmutableArray<DiffHunk> hunks, int added, int removed)
    {
        Hunks = hunks;
        Added = added;
        Removed = removed;
    }

    public ImmutableArray<DiffHunk> Hunks { get; }

    public int Added { get; }

    public int Removed { get; }

    public bool IsIdentical => Hunks.IsEmpty;

    public static UnifiedDiff Create(TextContent oldText, TextContent newText, int context = DiffLimits.DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        return Create(oldText.Lines, oldText.EndsWithNewline, newText.Lines, newText.EndsWithNewline, context);
    }

    public static UnifiedDiff Create(IReadOnlyList<string> oldLines, bool oldEndsWithNewline, IReadOnlyList<string> newLines, bool newEndsWithNewline, int context = DiffLimits.DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        ArgumentOutOfRangeException.ThrowIfLessThan(context, DiffLimits.MinContext);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(context, DiffLimits.MaxContext);

        // A last line without a line break is a different line from the same text with one.
        IReadOnlyList<string> oldKeys = Keys(oldLines, oldEndsWithNewline);
        IReadOnlyList<string> newKeys = Keys(newLines, newEndsWithNewline);
        IReadOnlyList<Edit> script = LineDiff.Compute(oldKeys, newKeys);

        int oldLast = oldEndsWithNewline ? -1 : oldLines.Count - 1;
        int newLast = newEndsWithNewline ? -1 : newLines.Count - 1;

        List<int> changes = [];
        for (int p = 0; p < script.Count; p++)
        {
            if (script[p].Kind != EditKind.Equal)
            {
                changes.Add(p);
            }
        }

        ImmutableArray<DiffHunk>.Builder hunks = ImmutableArray.CreateBuilder<DiffHunk>();
        int index = 0;
        while (index < changes.Count)
        {
            int first = changes[index];
            int last = first;
            while (index + 1 < changes.Count && changes[index + 1] - last - 1 <= 2 * context)
            {
                last = changes[++index];
            }

            index++;
            hunks.Add(Hunk(script, Math.Max(0, first - context), Math.Min(script.Count - 1, last + context), oldLines, newLines, oldLast, newLast));
        }

        return new UnifiedDiff(
            hunks.ToImmutable(),
            script.Count(edit => edit.Kind == EditKind.Insert),
            script.Count(edit => edit.Kind == EditKind.Delete));
    }

    /// <summary>The plain patch (<c>---</c>/<c>+++</c>, hunks, no-newline markers), every line ending in <c>\n</c>.</summary>
    public string ToPatchText(string oldLabel, string newLabel)
    {
        StringBuilder patch = new();
        if (IsIdentical)
        {
            return string.Empty;
        }

        patch.Append("--- ").Append(oldLabel).Append('\n');
        patch.Append("+++ ").Append(newLabel).Append('\n');
        foreach (DiffHunk hunk in Hunks)
        {
            patch.Append(hunk.Header).Append('\n');
            foreach (DiffLine line in hunk.Lines)
            {
                patch.Append(line.Op).Append(line.Text).Append('\n');
                if (line.NoNewlineAfter)
                {
                    patch.Append(NoNewlineMarker).Append('\n');
                }
            }
        }

        return patch.ToString();
    }

    private static string[] Keys(IReadOnlyList<string> lines, bool endsWithNewline)
    {
        string[] keys = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            keys[i] = lines[i] + "\n";
        }

        if (!endsWithNewline && keys.Length > 0)
        {
            keys[^1] = lines[^1];
        }

        return keys;
    }

    private static DiffHunk Hunk(IReadOnlyList<Edit> script, int from, int to, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int oldLast, int newLast)
    {
        ImmutableArray<DiffLine>.Builder lines = ImmutableArray.CreateBuilder<DiffLine>(to - from + 1);
        int oldCount = 0, newCount = 0;
        for (int p = from; p <= to; p++)
        {
            Edit edit = script[p];
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    oldCount++;
                    newCount++;
                    lines.Add(new DiffLine(DiffLineKind.Context, oldLines[edit.OldIndex], edit.OldIndex == oldLast));
                    break;
                case EditKind.Delete:
                    oldCount++;
                    lines.Add(new DiffLine(DiffLineKind.Removed, oldLines[edit.OldIndex], edit.OldIndex == oldLast));
                    break;
                default:
                    newCount++;
                    lines.Add(new DiffLine(DiffLineKind.Added, newLines[edit.NewIndex], edit.NewIndex == newLast));
                    break;
            }
        }

        int oldStart = script[from].OldIndex;
        int newStart = script[from].NewIndex;
        return new DiffHunk(
            oldCount == 0 ? oldStart : oldStart + 1,
            oldCount,
            newCount == 0 ? newStart : newStart + 1,
            newCount,
            lines.ToImmutable());
    }
}
