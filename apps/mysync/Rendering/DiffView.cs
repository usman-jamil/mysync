using System.Globalization;
using System.Text;
using MySync.Application.Conflicts.Diff;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using Spectre.Console;

namespace MySync.Cli.Rendering;

/// <summary>
/// <c>mysync diff</c>. On a terminal that can redraw: colours, with file content escaped and control
/// characters shown visibly (<c>␛</c>), so a file can't style or drive the terminal. Otherwise plain
/// text in exactly the patch form, so <c>mysync diff x &gt; x.patch</c> is something <c>git apply</c> takes.
/// </summary>
internal static class DiffView
{
    public static void Render(DiffResponse response, bool statOnly)
    {
        ArgumentNullException.ThrowIfNull(response);

        bool color = ConsoleOutput.CanRedraw;
        using Output output = color ? new ColorOutput() : new PlainOutput();
        if (response.Items.IsEmpty)
        {
            output.Line("No open conflicts. Name a path to compare it with the share: mysync diff <path>", "green");
            return;
        }

        bool first = true;
        foreach (PathDiff item in response.Items)
        {
            if (!first && !statOnly)
            {
                output.Line(string.Empty);
            }

            first = false;
            if (statOnly)
            {
                RenderStat(output, item);
            }
            else
            {
                RenderItem(output, item);
            }
        }
    }

    private static void RenderItem(Output output, PathDiff item)
    {
        output.Line(Header(item), "bold");
        foreach (string note in item.Notes)
        {
            output.Line(note, "yellow");
        }

        bool twoSections = item.Sections.Length > 1;
        foreach (DiffSection section in item.Sections)
        {
            if (twoSections)
            {
                output.Line($"{Capitalize(section.Title)} ({Name(section.OldSide)} → {Name(section.NewSide)}):", "bold");
            }

            RenderOutcome(output, item, section);
        }

        RenderFooter(output, item);
    }

    private static void RenderStat(Output output, PathDiff item)
    {
        string summary = string.Join(
            ", ",
            item.Sections.Select(section => section.Outcome switch
            {
                TextOutcome text => $"+{text.Added} −{text.Removed}",
                BinaryOutcome => "binary",
                TooLargeOutcome => "too large",
                EolOnlyOutcome => "line endings only",
                ExecOnlyOutcome => "executable bit only",
                TypeMismatchOutcome => "file/folder",
                _ => "identical",
            }));
        output.Line($"{Display(item.Path)}  {summary}");
        if (item.Kind is not null)
        {
            RenderFooter(output, item);
        }
    }

    private static void RenderOutcome(Output output, PathDiff item, DiffSection section)
    {
        switch (section.Outcome)
        {
            case TextOutcome { Hunks.IsEmpty: true }:
                output.Line(item.Kind is null ? "No differences from the share." : "No differences.", "dim");
                break;
            case TextOutcome text:
                output.Patch(section.OldLabel, section.NewLabel, text);
                break;
            case BinaryOutcome:
                output.Line($"Binary files differ: {Short(section.OldSide, Side(item, section.OldSide))}  ·  {Short(section.NewSide, Side(item, section.NewSide))}");
                break;
            case TooLargeOutcome large:
                output.Line(
                    $"Too large for an inline diff ({Name(section.OldSide)} {Size(large.OldSize)}, {Name(section.NewSide)} {Size(large.NewSize)}). "
                    + "Save the share's version with --print remote and use your own diff tool.");
                break;
            case EolOnlyOutcome eol:
                output.Line($"Only line endings differ ({Name(section.OldSide)}: {Eol(eol.Old)}, {Name(section.NewSide)}: {Eol(eol.New)}).");
                break;
            case ExecOnlyOutcome exec:
                output.Line(
                    $"Contents are identical; only the executable bit differs ({Name(section.OldSide)}: {(exec.OldExecutable ? "x" : "-")}, {Name(section.NewSide)}: {(exec.NewExecutable ? "x" : "-")}).");
                break;
            case TypeMismatchOutcome type:
                output.Line(Capitalize(type.Description) + ".");
                break;
            default:
                output.Line(item.Kind is null ? "No differences from the share." : "No differences.", "dim");
                break;
        }
    }

    private static void RenderFooter(Output output, PathDiff item)
    {
        if (item.Kind is not { } kind)
        {
            return;
        }

        (string KeepLocal, string KeepRemote) labels = Labels(kind);
        int width = Math.Max(labels.KeepLocal.Length, labels.KeepRemote.Length) + 4; // "Keep theirs:   mysync …"
        output.Line($"{(labels.KeepLocal + ":").PadRight(width)}{ResolveCommand(item.Path, "local")}");
        output.Line($"{(labels.KeepRemote + ":").PadRight(width)}{ResolveCommand(item.Path, "remote")}");
        if (kind is ConflictKind.ModifyModify or ConflictKind.AddAdd && item.Sections.Any(section => section.Outcome is TextOutcome or EolOnlyOutcome))
        {
            output.Line("Or edit the file to combine both, then keep yours.", "dim");
        }
    }

    /// <summary>What the two resolve choices mean for this kind of conflict.</summary>
    public static (string KeepLocal, string KeepRemote) Labels(ConflictKind kind) => kind switch
    {
        ConflictKind.DeleteModify => ("Keep it deleted", "Restore theirs"),
        ConflictKind.ModifyDelete => ("Keep yours", "Delete it"),
        _ => ("Keep yours", "Keep theirs"),
    };

    public static string ResolveCommand(SharePath path, string keep) => $"mysync resolve {ShellQuote.Quote(path.Value)} --keep {keep}";

    public static string DiffCommand(SharePath path) => $"mysync diff {ShellQuote.Quote(path.Value)}";

    private static string Header(PathDiff item)
    {
        string kind = item.Kind?.ToCode() ?? "not in conflict";
        string local = item.Local is { } l ? $"local {Size(l.Size)} (you, {When(l.MtimeUtc)})" : "local: none";
        string absentRemote = item.Kind is null ? "remote: none" : "remote: deleted";
        string remote = item.Remote is { } r
            ? $"remote {Size(r.Size)} ({PathErrors.Display(r.UpdatedBy ?? "?")}, rev {r.Revision?.Value}, {When(r.MtimeUtc)})"
            : absentRemote;
        if (item.Local?.Type == EntryType.Directory)
        {
            local = "local: a folder";
        }

        if (item.Remote?.Type == EntryType.Directory)
        {
            remote = "remote: a folder";
        }

        return $"{Display(item.Path)}  {kind}  {local}  ·  {remote}";
    }

    private static DiffSide? Side(PathDiff item, DiffSideKind kind) => kind switch
    {
        DiffSideKind.Local => item.Local,
        DiffSideKind.Remote => item.Remote,
        _ => item.Base,
    };

    private static string Short(DiffSideKind kind, DiffSide? side) => side?.Sha256 is { } hash
        ? $"{Name(kind)} {Size(side.Size)} (sha256 {hash.Value[..6]}…)"
        : $"{Name(kind)}: none";

    private static string Size(long? bytes) => bytes is { } value ? ConsoleOutput.Bytes(value) : "-";

    private static string When(DateTimeOffset? utc)
    {
        if (utc is not { } value)
        {
            return "-";
        }

        DateTimeOffset local = value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Eol(LineEndingStyle style) => style switch
    {
        LineEndingStyle.CrLf => "CRLF",
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.Cr => "CR",
        LineEndingStyle.Mixed => "mixed",
        _ => "none",
    };

    private static string Name(DiffSideKind kind) => kind switch
    {
        DiffSideKind.Local => "local",
        DiffSideKind.Remote => "remote",
        _ => "base",
    };

    private static string Display(SharePath path) => PathErrors.Display(path.Value);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private abstract class Output : IDisposable
    {
        public abstract void Line(string text, string? style = null);

        public abstract void Patch(string oldLabel, string newLabel, TextOutcome text);

        public virtual void Dispose()
        {
        }
    }

    /// <summary>Colours via Spectre; everything from the file escaped and made visible first.</summary>
    private sealed class ColorOutput : Output
    {
        public override void Line(string text, string? style = null) =>
            AnsiConsole.MarkupLine(style is null ? Markup.Escape(text) : $"[{style}]{Markup.Escape(text)}[/]");

        public override void Patch(string oldLabel, string newLabel, TextOutcome text)
        {
            AnsiConsole.MarkupLine($"[bold]--- {Markup.Escape(TerminalText.Visible(oldLabel))}[/]");
            AnsiConsole.MarkupLine($"[bold]+++ {Markup.Escape(TerminalText.Visible(newLabel))}[/]");
            foreach (DiffHunk hunk in text.Hunks)
            {
                AnsiConsole.MarkupLine($"[cyan]{hunk.Header}[/]");
                foreach (DiffLine line in hunk.Lines)
                {
                    string content = Markup.Escape(line.Op + TerminalText.Visible(line.Text));
                    AnsiConsole.MarkupLine(line.Kind switch
                    {
                        DiffLineKind.Removed => $"[red]{content}[/]",
                        DiffLineKind.Added => $"[green]{content}[/]",
                        _ => content,
                    });
                    if (line.NoNewlineAfter)
                    {
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(UnifiedDiff.NoNewlineMarker)}[/]");
                    }
                }
            }
        }
    }

    /// <summary>Raw UTF-8 on stdout, <c>\n</c> line ends, nothing escaped or changed: a patch.</summary>
    private sealed class PlainOutput : Output
    {
        private readonly StreamWriter _writer = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };

        public override void Line(string text, string? style = null) => _writer.WriteLine(text);

        public override void Patch(string oldLabel, string newLabel, TextOutcome text)
        {
            _writer.WriteLine($"--- {oldLabel}");
            _writer.WriteLine($"+++ {newLabel}");
            foreach (DiffHunk hunk in text.Hunks)
            {
                _writer.WriteLine(hunk.Header);
                foreach (DiffLine line in hunk.Lines)
                {
                    _writer.Write(line.Op);
                    _writer.Write(line.Text);
                    _writer.Write('\n');
                    if (line.NoNewlineAfter)
                    {
                        _writer.WriteLine(UnifiedDiff.NoNewlineMarker);
                    }
                }
            }
        }

        public override void Dispose()
        {
            _writer.Dispose();
            base.Dispose();
        }
    }
}
