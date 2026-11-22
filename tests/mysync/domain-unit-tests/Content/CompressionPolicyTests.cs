using MySync.Domain.Content;
using MySync.Domain.Manifests;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Content;

/// <summary>Which files are stored compressed (CLAUDE.md §6).</summary>
public sealed class CompressionPolicyTests
{
    private static readonly CompressionSample Shrinks = new(1000, 300);

    [Fact]
    public void CompressionOff_Should_NeverCompress()
    {
        CompressionPolicy.NeedsSample(CompressionSetting.None, Path("big.log"), 10_000_000).ShouldBeFalse();
        CompressionPolicy.Decide(CompressionSetting.None, Path("big.log"), 10_000_000, Shrinks).ShouldBe(ObjectEncoding.Identity);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(511, false)]
    [InlineData(512, true)]
    [InlineData(513, true)]
    public void SmallFiles_Should_StayRaw(long size, bool sampled)
    {
        CompressionPolicy.NeedsSample(CompressionSetting.Gzip, Path("a.log"), size).ShouldBe(sampled);
        CompressionPolicy.Decide(CompressionSetting.Gzip, Path("a.log"), size, Shrinks).ShouldBe(sampled ? ObjectEncoding.Gzip : ObjectEncoding.Identity);
    }

    [Theory]
    [InlineData("backup.zip")]
    [InlineData("dist/app.tar.gz")]
    [InlineData("PHOTO.JPG")]
    [InlineData("deck.PpTx")]
    [InlineData("a/b/report.pdf")]
    [InlineData("font.woff2")]
    [InlineData("movie.webm")]
    [InlineData("lib.nupkg")]
    public void AlreadyCompressedTypes_Should_StayRaw_WhateverTheSample(string path)
    {
        CompressionPolicy.IsPrecompressed(Path(path)).ShouldBeTrue();
        CompressionPolicy.NeedsSample(CompressionSetting.Gzip, Path(path), 10_000_000).ShouldBeFalse();
        CompressionPolicy.Decide(CompressionSetting.Gzip, Path(path), 10_000_000, Shrinks).ShouldBe(ObjectEncoding.Identity);
    }

    [Theory]
    [InlineData("server.log")]
    [InlineData("data.csv")]
    [InlineData("schema.sql")]
    [InlineData("Makefile")]
    [InlineData("archive.zip.txt")]
    [InlineData("trailing.")]
    [InlineData(".gitignore")]
    public void OtherFiles_Should_BeSampled(string path)
    {
        CompressionPolicy.IsPrecompressed(Path(path)).ShouldBeFalse();
        CompressionPolicy.NeedsSample(CompressionSetting.Gzip, Path(path), 4096).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1000, 900, ObjectEncoding.Gzip)]      // exactly 90%: worth it
    [InlineData(1000, 901, ObjectEncoding.Identity)]  // just over 90%: not
    [InlineData(1000, 1020, ObjectEncoding.Identity)] // grew
    [InlineData(1000, 100, ObjectEncoding.Gzip)]
    [InlineData(0, 0, ObjectEncoding.Identity)]       // no sample
    public void TheSample_Should_Decide(int sampleLength, long compressedLength, ObjectEncoding expected)
    {
        CompressionPolicy.Decide(CompressionSetting.Gzip, Path("data.csv"), 4096, new CompressionSample(sampleLength, compressedLength)).ShouldBe(expected);
    }

    [Fact]
    public void TheConstants_Should_BeThePromisedOnes()
    {
        CompressionPolicy.MinimumSize.ShouldBe(512);
        CompressionPolicy.SampleBytes.ShouldBe(1024 * 1024);
        ((double)CompressionPolicy.MaxRatioNumerator / CompressionPolicy.MaxRatioDenominator).ShouldBe(0.9);
    }
}
