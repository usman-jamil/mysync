using System.Diagnostics;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.Ignore;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Ignore;

/// <summary>
/// <c>.mysyncignore</c> matching (CLAUDE.md §7). The reference is git itself: the corpus in
/// <c>gitignore-cases.txt</c> is checked against <c>git</c> on every CI OS, and the hand-written
/// cases below pin the behaviour where git isn't installed.
/// </summary>
public class GitIgnoreMatcherTests
{
    private const string Probe = ".probe-file";

    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "deep/dir/b.log", false, true)]
    [InlineData("*.log", "a.logx", false, false)]
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("build/", "build", false, false)]               // directory-only
    [InlineData("build/", "src/build", true, true)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]          // a middle slash anchors
    [InlineData("docs/*.md", "x/docs/a.md", false, false)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]     // * stops at /
    [InlineData("**/foo", "a/b/foo", false, true)]
    [InlineData("a/**/b", "a/b", false, true)]
    [InlineData("a/**/b", "a/x/y/b", false, true)]
    [InlineData("a/**/b", "a/xb", false, false)]
    [InlineData("abc/**", "abc/x/y", false, true)]
    [InlineData("abc/**", "abc", true, true)]                    // the directory itself too, as git does
    [InlineData("abc/**", "abc", false, false)]                  // but not a file of that name
    [InlineData("?.md", "a.md", false, true)]
    [InlineData("?.md", "ab.md", false, false)]
    [InlineData("[!a]*.txt", "a.txt", false, false)]
    [InlineData("[!a]*.txt", "b.txt", false, true)]
    [InlineData("file[0-9].txt", "file7.txt", false, true)]
    [InlineData("[[:digit:]]*", "9lives", false, true)]
    [InlineData("\\#notes", "#notes", false, true)]
    [InlineData("\\!important", "!important", false, true)]
    [InlineData("*.LOG", "a.log", false, false)]                 // case-sensitive everywhere
    [InlineData("# comment", "# comment", false, false)]
    [InlineData("café/", "café", true, true)]         // NFD pattern text or path, same thing
    public void Pattern_Should_MatchLikeGit(string pattern, string path, bool isDirectory, bool ignored)
    {
        new GitIgnoreMatcher([pattern]).IsIgnored(P(path), isDirectory).ShouldBe(ignored);
    }

    [Fact]
    public void TheLastMatchingRule_Should_Win()
    {
        GitIgnoreMatcher matcher = new(["*.txt", "!keep.txt", "keep.txt"]);

        matcher.IsIgnored(P("keep.txt"), isDirectory: false).ShouldBeTrue();
        new GitIgnoreMatcher(["*.txt", "!keep.txt"]).IsIgnored(P("keep.txt"), isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void AFileInsideAnIgnoredDirectory_Should_StayIgnored_EvenIfARuleReincludesIt()
    {
        GitIgnoreMatcher matcher = new(["logs/", "!logs/keep.log"]);

        matcher.IsIgnored(P("logs/keep.log"), isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void TrailingSpaces_Should_BeDropped_UnlessEscaped()
    {
        new GitIgnoreMatcher(["spaced.txt   "]).IsIgnored(P("spaced.txt"), isDirectory: false).ShouldBeTrue();
        new GitIgnoreMatcher(["x\\ "]).IsIgnored(P("x "), isDirectory: false).ShouldBeTrue();
        new GitIgnoreMatcher(["x\\ "]).IsIgnored(P("x"), isDirectory: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData(".mysynctool")]
    [InlineData(".mysynctool.lock")]
    [InlineData(".mysynctool.tmp")]
    [InlineData("nested/share/.mysynctool")]
    [InlineData(".DS_Store")]
    [InlineData("photos/Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("docs/._report.pdf")]
    public void BuiltIns_Should_BeIgnoredAtAnyDepth_AndNotBeReincludable(string path)
    {
        GitIgnoreMatcher matcher = new(["!*", "!.*", $"!{Path.GetFileName(path)}"]);

        matcher.IsIgnored(P(path), isDirectory: false).ShouldBeTrue();
        GitIgnoreMatcher.BuiltInsOnly.IsIgnored(P(path), isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void TheIgnoreFileItself_Should_NotBeABuiltIn_BecauseItIsSynced()
    {
        GitIgnoreMatcher.BuiltInsOnly.IsIgnored(P(StateFileNames.IgnoreFile), isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void APathologicalPattern_Should_StillMatchInLinearTime()
    {
        // With a backtracking engine this takes exponential time; .mysyncignore comes from other machines.
        GitIgnoreMatcher matcher = new([string.Concat(Enumerable.Repeat("*a", 30)) + "b"]);
        string path = new('a', 5000);

        var timer = Stopwatch.StartNew();
        matcher.IsIgnored(P(path), isDirectory: false).ShouldBeFalse();
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!")]
    [InlineData("/")]
    [InlineData("a//b")]
    [InlineData("[[:nonsense:]]")]
    [InlineData("[abc")]
    public void ALineThatMatchesNothing_Should_BeHarmless(string line)
    {
        GitIgnoreMatcher matcher = new([line]);

        matcher.IsIgnored(P("a"), isDirectory: false).ShouldBeFalse();
        matcher.IsIgnored(P("a/b"), isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void EveryCorpusCase_Should_AgreeWithGit()
    {
        Assert.SkipUnless(GitIsAvailable(), "git is not installed");
        List<string> disagreements = [];
        int checkedCases = 0;

        foreach (Scenario scenario in Scenario.Load(Path.Combine(AppContext.BaseDirectory, "Ignore", "gitignore-cases.txt")))
        {
            GitIgnoreMatcher matcher = new(scenario.Rules);
            foreach ((string path, bool isDirectory) in scenario.Paths)
            {
                if (OperatingSystem.IsWindows() && PathRules.CheckWindowsCompatible(P(path)).IsFailure)
                {
                    continue; // e.g. "x ": Windows can't create it, so git there can't be asked
                }

                checkedCases++;
                (bool Git, bool Ours, string What)[] comparisons = isDirectory
                    ?
                    [
                        // The directory itself, and whether a file inside it would be synced.
                        (GitCheckIgnore(scenario.Rules, path), matcher.IsIgnored(P(path), isDirectory: true), "itself"),
                        (GitIgnoresFileIn(scenario.Rules, path), matcher.IsIgnored(P($"{path}/{Probe}"), isDirectory: false), "its contents"),
                    ]
                    : [(GitIgnoresFile(scenario.Rules, path), matcher.IsIgnored(P(path), isDirectory: false), "")];

                disagreements.AddRange(comparisons
                    .Where(comparison => comparison.Git != comparison.Ours)
                    .Select(comparison => $"[{scenario.Name}] '{path}'{(isDirectory ? "/" : "")} {comparison.What}: git {(comparison.Git ? "ignores" : "keeps")} it, we {(comparison.Ours ? "ignore" : "keep")} it"));
            }
        }

        checkedCases.ShouldBeGreaterThan(80);
        disagreements.ShouldBeEmpty(string.Join(System.Environment.NewLine, disagreements));
    }

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);

    private static bool GitIsAvailable()
    {
        try
        {
            return Git(Path.GetTempPath(), "--version").ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Each question gets its own repository, so a file and a directory of the same name can both be asked about.
    private static bool GitIgnoresFile(IReadOnlyList<string> rules, string path) => InRepository(rules, root =>
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return !ListedUntracked(root).Contains(path);
    });

    private static bool GitIgnoresFileIn(IReadOnlyList<string> rules, string directory) => InRepository(rules, root =>
    {
        Directory.CreateDirectory(Path.Combine(root, directory));
        File.WriteAllText(Path.Combine(root, directory, Probe), "x");
        return !ListedUntracked(root).Contains($"{directory}/{Probe}");
    });

    private static bool GitCheckIgnore(IReadOnlyList<string> rules, string directory) => InRepository(rules, root =>
    {
        Directory.CreateDirectory(Path.Combine(root, directory));
        return Git(root, "-c", "core.ignorecase=false", "check-ignore", "-q", "--no-index", "--", directory + "/").ExitCode == 0;
    });

    private static HashSet<string> ListedUntracked(string root) =>
        [.. Git(root, "-c", "core.ignorecase=false", "-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard", "-z")
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(PathRules.ToNfc)];

    private static bool InRepository(IReadOnlyList<string> rules, Func<string, bool> question)
    {
        using TempShareFolder repository = new();
        Git(repository.Path, "init", "-q");
        File.WriteAllText(Path.Combine(repository.Path, ".gitignore"), string.Join("\n", rules) + "\n");
        return question(repository.Path);
    }

    private static (int ExitCode, string Output) Git(string directory, params string[] arguments)
    {
        // UTF-8 explicitly: on Windows the default would be the console code page, which mangles "café".
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        git.StandardError.ReadToEnd();
        git.WaitForExit();
        return (git.ExitCode, output);
    }

    private sealed record Scenario(string Name, IReadOnlyList<string> Rules, IReadOnlyList<(string Path, bool IsDirectory)> Paths)
    {
        // "=== name", rule lines, "---", then one path per line (a trailing / marks a directory).
        public static IEnumerable<Scenario> Load(string file)
        {
            string[] lines = [.. File.ReadAllLines(file).Select(line => line.TrimEnd('\r'))];
            int i = 0;
            while (i < lines.Length)
            {
                if (!lines[i].StartsWith("=== ", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                string name = lines[i][4..];
                List<string> rules = [];
                for (i++; lines[i] != "---"; i++)
                {
                    rules.Add(lines[i]);
                }

                List<(string, bool)> paths = [];
                for (i++; i < lines.Length && !lines[i].StartsWith("=== ", StringComparison.Ordinal); i++)
                {
                    if (lines[i].Length > 0)
                    {
                        paths.Add((lines[i].TrimEnd('/'), lines[i].EndsWith('/')));
                    }
                }

                yield return new Scenario(name, rules, paths);
            }
        }
    }
}
