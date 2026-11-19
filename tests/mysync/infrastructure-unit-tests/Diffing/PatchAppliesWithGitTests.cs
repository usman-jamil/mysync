using System.Diagnostics;
using System.Text;
using MySync.Domain.Diffing;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Diffing;

/// <summary>
/// The patch <c>mysync diff</c> prints is a real patch: <c>git apply</c> turns A into B byte for byte.
/// Our hunks may differ from git's own (Myers breaks ties differently); what's pinned is that ours apply.
/// </summary>
public sealed class PatchAppliesWithGitTests
{
    private static readonly Lazy<bool> GitAvailable = new(() =>
    {
        try
        {
            return Git(Path.GetTempPath(), [], "--version").ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    public static TheoryData<string, string, string> Fixtures => new()
    {
        { "one line changed", "a\nb\nc\n", "a\nB\nc\n" },
        { "lines added and removed", "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n", "1\n3\n4\nnew\n5\n6\n7\n8\n9\n10\n12\n13\n" },
        { "old file has no final newline", "a\nb", "a\nb\nc\n" },
        { "new file has no final newline", "a\nb\n", "a\nc" },
        { "neither has a final newline", "x\na", "y\na" },
        { "only the final newline added", "a\nb", "a\nb\n" },
        { "crlf file", "a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n" },
        { "crlf, no final newline", "a\r\nb", "a\r\nc" },
        { "empty to content", "", "a\nb\n" },
        { "content to empty", "a\nb\n", "" },
        { "only line endings", "a\nb\n", "a\r\nb\r\n" },
        { "unicode", "café\nnaïve\n", "café\nnaive\n" },
        { "a line that looks like a header", "--- x\n+++ y\n@@ -1 +1 @@\n", "--- x\n+++ z\n@@ -1 +1 @@\n" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ThePatch_Should_TurnAIntoB_WithGitApply(string name, string oldText, string newText)
    {
        Assert.SkipUnless(GitAvailable.Value, "git is not installed");

        foreach (int context in new[] { 0, 1, 3 })
        {
            ApplyAndRead(oldText, newText, context).ShouldBe(Encoding.UTF8.GetBytes(newText), $"{name}, context {context}");
        }
    }

    [Fact]
    public void RandomPatches_Should_ApplyWithGit()
    {
        Assert.SkipUnless(GitAvailable.Value, "git is not installed");
        ulong state = 7;
        int Next(int max)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((state >> 33) % (ulong)max);
        }

        for (int round = 0; round < 25; round++)
        {
            string oldText = string.Concat(Enumerable.Range(0, Next(40)).Select(_ => "abcd"[Next(4)] + "\n"));
            string newText = string.Concat(Enumerable.Range(0, Next(40)).Select(_ => "abcd"[Next(4)] + "\n"));
            ApplyAndRead(oldText, newText, Next(4)).ShouldBe(Encoding.UTF8.GetBytes(newText), $"round {round}");
        }
    }

    private static byte[] ApplyAndRead(string oldText, string newText, int context)
    {
        var diff = UnifiedDiff.Create(TextContent.From(Encoding.UTF8.GetBytes(oldText)), TextContent.From(Encoding.UTF8.GetBytes(newText)), context);
        using TempShareFolder folder = new();
        string file = Path.Combine(folder.Path, "f.txt");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(oldText));
        if (diff.IsIdentical)
        {
            return File.ReadAllBytes(file);
        }

        string patch = Path.Combine(folder.Path, "change.patch");
        File.WriteAllBytes(patch, Encoding.UTF8.GetBytes(diff.ToPatchText("local/f.txt", "remote/f.txt")));

        List<string> arguments = ["apply", "--whitespace=nowarn"];
        if (context == 0)
        {
            arguments.Add("--unidiff-zero");
        }

        arguments.Add(patch);
        (int exitCode, string error) = Git(folder.Path, ["-c", "core.autocrlf=false"], [.. arguments]);
        exitCode.ShouldBe(0, $"git apply failed: {error}\n{File.ReadAllText(patch)}");
        return File.ReadAllBytes(file);
    }

    private static (int ExitCode, string Error) Git(string directory, string[] options, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in options.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using Process git = Process.Start(start)!;
        git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        return (git.ExitCode, error);
    }
}
