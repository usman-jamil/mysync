using System.Globalization;
using System.IO.Compression;
using System.Text;
using MySync.Domain.Paths.Normalization;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Paths.Normalization;

/// <summary>
/// Pins <see cref="NfcNormalizer"/> against the Unicode conformance file (NormalizationTest.txt,
/// same UCD version as the generated tables; refreshed by tools/unicode/GenerateNfcData.cs).
/// </summary>
public class NfcNormalizerTests
{
    private static readonly Lazy<ConformanceData> Conformance = new(ConformanceData.Load);

    [Fact]
    public void ConformanceFile_Should_MatchTheGeneratedTablesVersion()
    {
        Conformance.Value.Version.ShouldBe(NfcNormalizer.UnicodeVersion);
    }

    [Fact]
    public void Normalize_Should_PassEveryConformanceCase()
    {
        List<string> failures = [];

        foreach (ConformanceCase testCase in Conformance.Value.Cases)
        {
            // NFC invariants from the file header: c2 == toNFC(c1) == toNFC(c2) == toNFC(c3),
            // c4 == toNFC(c4) == toNFC(c5).
            Check(testCase, testCase.Source, testCase.Nfc, failures);
            Check(testCase, testCase.Nfc, testCase.Nfc, failures);
            Check(testCase, testCase.Nfd, testCase.Nfc, failures);
            Check(testCase, testCase.Nfkc, testCase.Nfkc, failures);
            Check(testCase, testCase.Nfkd, testCase.Nfkc, failures);
        }

        Conformance.Value.Cases.Count.ShouldBeGreaterThan(19_000);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(20)));
    }

    [Fact]
    public void Normalize_Should_LeaveEveryCodePointNotInPart1Unchanged()
    {
        List<string> failures = [];

        for (int codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF || Conformance.Value.Part1CodePoints.Contains(codePoint))
            {
                continue;
            }

            string value = char.ConvertFromUtf32(codePoint);

            if (!string.Equals(NfcNormalizer.Normalize(value), value, StringComparison.Ordinal))
            {
                failures.Add(codePoint.ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        failures.ShouldBeEmpty(string.Join(", ", failures.Take(20)));
    }

    [Fact]
    public void FastPath_Should_BeSound()
    {
        // Normalize returns early when every char is below FirstNonTrivialCharacter. That is only
        // correct if no such character changes on its own or is ever composed onto a starter.
        for (int codePoint = 0; codePoint < NfcNormalizer.FirstNonTrivialCharacter; codePoint++)
        {
            string value = char.ConvertFromUtf32(codePoint);

            NfcNormalizer.NormalizeCore(value).ShouldBe(value);
            NfcNormalizer.GetCombiningClass(codePoint).ShouldBe(0);
        }

        ReadOnlySpan<int> compositions = UnicodeNormalizationData.Compositions;
        for (int i = 0; i < compositions.Length; i += 3)
        {
            compositions[i + 1].ShouldBeGreaterThanOrEqualTo(NfcNormalizer.FirstNonTrivialCharacter);
        }
    }

    [Theory]
    [InlineData("docs/café.md", "docs/café.md")]
    [InlineData("각", "각")] // Hangul jamo L+V+T -> syllable
    [InlineData("ậ", "ậ")] // combining marks out of canonical order: ậ
    [InlineData("ậ", "ậ")]
    [InlineData("Å", "Å")] // ANGSTROM SIGN is a singleton, never recomposed
    [InlineData("𝅗𝅥", "𝅗𝅥")] // MUSICAL SYMBOL HALF NOTE: composition exclusion
    public void Normalize_Should_ProduceNfc(string input, string expected)
    {
        NfcNormalizer.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("plain/ascii.txt")]
    [InlineData("café/über.txt")]
    [InlineData("日本語/한글.txt")]
    public void Normalize_Should_ReturnTheSameInstance_WhenAlreadyNfc(string input)
    {
        NfcNormalizer.Normalize(input).ShouldBeSameAs(input);
    }

    // Not [InlineData]: attribute strings are stored as UTF-8, so an unpaired surrogate would
    // arrive as U+FFFD and the test would silently check something else.
    [Fact]
    public void Normalize_Should_PassUnpairedSurrogatesThrough()
    {
        const char high = '\uD800';
        const char low = '\uDC00';

        NfcNormalizer.Normalize("a" + high + "b").ShouldBe("a" + high + "b");
        NfcNormalizer.Normalize(low + "e\u0301").ShouldBe(low + "\u00E9");
        NfcNormalizer.Normalize("e\u0301" + high).ShouldBe("\u00E9" + high);
        NfcNormalizer.Normalize("a" + low + high + "b").ShouldBe("a" + low + high + "b"); // reversed pair stays unpaired
    }

    private static void Check(ConformanceCase testCase, string input, string expected, List<string> failures)
    {
        string actual = NfcNormalizer.Normalize(input);

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            failures.Add($"line {testCase.LineNumber}: {Hex(input)} -> {Hex(actual)}, expected {Hex(expected)}");
        }
    }

    private static string Hex(string value)
    {
        StringBuilder builder = new();

        foreach (Rune rune in value.EnumerateRunes())
        {
            builder.Append(CultureInfo.InvariantCulture, $"{rune.Value:X4} ");
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record ConformanceCase(int LineNumber, string Source, string Nfc, string Nfd, string Nfkc, string Nfkd);

    private sealed record ConformanceData(string Version, List<ConformanceCase> Cases, HashSet<int> Part1CodePoints)
    {
        public static ConformanceData Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Paths", "Normalization", "NormalizationTest.txt.gz");

            using FileStream file = File.OpenRead(path);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);

            string version = "";
            string part = "";
            List<ConformanceCase> cases = [];
            HashSet<int> part1 = [];
            int lineNumber = 0;

            while (reader.ReadLine() is { } line)
            {
                lineNumber++;

                if (line.StartsWith("# NormalizationTest-", StringComparison.Ordinal))
                {
                    version = line["# NormalizationTest-".Length..].Replace(".txt", "", StringComparison.Ordinal);
                    continue;
                }

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '@')
                {
                    part = line.Split(' ')[0];
                    continue;
                }

                string[] columns = line.Split(';');
                ConformanceCase testCase = new(
                    lineNumber, Decode(columns[0]), Decode(columns[1]), Decode(columns[2]), Decode(columns[3]), Decode(columns[4]));
                cases.Add(testCase);

                if (part == "@Part1")
                {
                    part1.Add(char.ConvertToUtf32(testCase.Source, 0));
                }
            }

            return new ConformanceData(version, cases, part1);
        }

        private static string Decode(string column)
        {
            StringBuilder builder = new();

            foreach (string hex in column.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append(char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            return builder.ToString();
        }
    }
}
