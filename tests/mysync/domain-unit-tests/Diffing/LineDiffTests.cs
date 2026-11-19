using MySync.Domain.Diffing;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Diffing;

public sealed class LineDiffTests
{
    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Script_Should_TurnAIntoB_AndBeMinimal(int seed)
    {
        var random = new Lcg(seed); // a fixed seed per case, so a failure reproduces
        for (int round = 0; round < 50; round++)
        {
            string[] a = RandomLines(random);
            string[] b = random.Next(4) == 0 ? Mutate(a, random) : RandomLines(random);

            IReadOnlyList<Edit> script = LineDiff.Compute(a, b);

            string.Join(",", Apply(a, b, script)).ShouldBe(string.Join(",", b), $"seed {seed}, round {round}");
            int edits = script.Count(edit => edit.Kind != EditKind.Equal);
            edits.ShouldBe(a.Length + b.Length - 2 * Lcs(a, b), $"seed {seed}, round {round}");
        }
    }

    [Fact]
    public void Script_Should_DeleteBeforeInsert_WithinAChange()
    {
        IReadOnlyList<Edit> script = LineDiff.Compute(["a", "x", "c"], ["a", "y", "c"]);

        script.Select(edit => edit.Kind).ShouldBe([EditKind.Equal, EditKind.Delete, EditKind.Insert, EditKind.Equal]);
    }

    [Fact]
    public void Script_Should_StillBeCorrect_WhenTheSearchBudgetRunsOut()
    {
        string[] a = [.. Enumerable.Range(0, 300).Select(i => (i % 7).ToString(System.Globalization.CultureInfo.InvariantCulture))];
        string[] b = [.. Enumerable.Range(0, 300).Select(i => (i % 5).ToString(System.Globalization.CultureInfo.InvariantCulture))];

        IReadOnlyList<Edit> script = LineDiff.Compute(a, b, maxSearchSteps: 10);

        Apply(a, b, script).ShouldBe(b);
    }

    [Fact]
    public void Script_Should_CompareLinesOrdinally()
    {
        IReadOnlyList<Edit> script = LineDiff.Compute(["straße", "A"], ["strasse", "a"]);

        script.Count(edit => edit.Kind == EditKind.Equal).ShouldBe(0);
    }

    [Fact]
    public void ALargeFile_WithOneChange_Should_BeQuick()
    {
        string[] a = [.. Enumerable.Range(0, 200_000).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        string[] b = [.. a];
        b[100_000] = "changed";

        IReadOnlyList<Edit> script = LineDiff.Compute(a, b);

        script.Count(edit => edit.Kind != EditKind.Equal).ShouldBe(2);
    }

    private static string[] RandomLines(Lcg random) =>
        [.. Enumerable.Range(0, random.Next(0, random.Next(2) == 0 ? 12 : 200)).Select(_ => ((char)('a' + random.Next(4))).ToString())];

    private static string[] Mutate(string[] a, Lcg random)
    {
        List<string> b = [.. a];
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            int at = random.Next(b.Count + 1);
            if (random.Next(2) == 0 && at < b.Count)
            {
                b.RemoveAt(at);
            }
            else
            {
                b.Insert(at, ((char)('a' + random.Next(4))).ToString());
            }
        }

        return [.. b];
    }

    /// <summary>Applies the script to <paramref name="a"/>, checking every equal and deleted line really is A's.</summary>
    private static List<string> Apply(string[] a, string[] b, IReadOnlyList<Edit> script)
    {
        List<string> result = [];
        int i = 0;
        foreach (Edit edit in script)
        {
            switch (edit.Kind)
            {
                case EditKind.Equal:
                    edit.OldIndex.ShouldBe(i);
                    a[i].ShouldBe(b[edit.NewIndex]);
                    result.Add(a[i++]);
                    break;
                case EditKind.Delete:
                    edit.OldIndex.ShouldBe(i++);
                    break;
                default:
                    edit.NewIndex.ShouldBe(result.Count);
                    result.Add(b[edit.NewIndex]);
                    break;
            }
        }

        i.ShouldBe(a.Length);
        return result;
    }

    /// <summary>Longest common subsequence, by the plain dynamic program: the reference for minimality.</summary>
    private static int Lcs(string[] a, string[] b)
    {
        int[][] table = [.. Enumerable.Range(0, a.Length + 1).Select(_ => new int[b.Length + 1])];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                table[i][j] = a[i] == b[j] ? table[i + 1][j + 1] + 1 : Math.Max(table[i + 1][j], table[i][j + 1]);
            }
        }

        return table[0][0];
    }

    /// <summary>A small fixed-seed generator (SplitMix64), so the test data is the same on every run and OS.</summary>
    private sealed class Lcg(int seed)
    {
        private ulong _state = (ulong)seed;

        public int Next(int maxExclusive) => Next(0, maxExclusive);

        public int Next(int minInclusive, int maxExclusive)
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(z % (ulong)(maxExclusive - minInclusive));
        }
    }
}
