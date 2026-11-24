using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>The multipart arithmetic at S3's limits (5 MiB to 5 GiB parts, 10,000 parts, 5 TiB objects).</summary>
public class S3PartSizesTests
{
    private const long MiB = S3Limits.MiB;
    private const long GiB = S3Limits.GiB;
    private const long TiB = S3Limits.TiB;

    [Theory]
    [InlineData(0L, 16 * MiB)]
    [InlineData(1L, 16 * MiB)]
    [InlineData(160_000L * MiB, 16 * MiB)] // exactly 10,000 parts of 16 MiB
    [InlineData(160_000L * MiB + 1, 16 * MiB + 1)] // one byte more: the parts grow
    [InlineData(5 * TiB, 549_755_814L)] // ceil(5 TiB / 10,000)
    public void AKnownLength_Should_FitIn10000Parts_OfAtLeast16MiB(long length, long expected)
    {
        long size = S3PartSizes.ForKnownLength(length);

        size.ShouldBe(expected);
        ((length + size - 1) / size).ShouldBeLessThanOrEqualTo(S3Limits.MaxParts);
        size.ShouldBeLessThanOrEqualTo(S3Limits.MaxPartSize);
    }

    [Fact]
    public void AnUnknownLength_Should_DoubleEvery1000Parts_UpTo1GiB()
    {
        S3PartSizes.ForPart(1, 16 * MiB).ShouldBe(16 * MiB);
        S3PartSizes.ForPart(1000, 16 * MiB).ShouldBe(16 * MiB);
        S3PartSizes.ForPart(1001, 16 * MiB).ShouldBe(32 * MiB);
        S3PartSizes.ForPart(2001, 16 * MiB).ShouldBe(64 * MiB);
        S3PartSizes.ForPart(6001, 16 * MiB).ShouldBe(1 * GiB);
        S3PartSizes.ForPart(10_000, 16 * MiB).ShouldBe(1 * GiB);
    }

    [Fact]
    public void TheMostAnUnknownLengthCanHold_Should_BeAbout4Point87TiB_AndInsideS3sLimit()
    {
        long max = S3PartSizes.MaxUnknownLength();

        // 1,000 parts each of 16, 32, 64, 128, 256 and 512 MiB, then 4,000 of 1 GiB.
        max.ShouldBe((16 + 32 + 64 + 128 + 256 + 512) * 1000 * MiB + 4000 * GiB);
        max.ShouldBeLessThan(S3Limits.MaxObjectSize);
        (max / (double)TiB).ShouldBe(4.87, tolerance: 0.01);
    }

    [Fact]
    public void EveryPart_Should_StayInsideS3sPartLimits()
    {
        for (int part = 1; part <= S3Limits.MaxParts; part += 97)
        {
            long size = S3PartSizes.ForPart(part, 16 * MiB);
            size.ShouldBeGreaterThanOrEqualTo(S3Limits.MinPartSize);
            size.ShouldBeLessThanOrEqualTo(S3Limits.MaxPartSize);
        }
    }

    [Theory]
    [InlineData(6 * GiB, 512 * MiB)]
    [InlineData(5 * TiB, 549_755_814L)]
    public void ACopy_Should_UseParts_OfAtLeast512MiB_ThatFit(long length, long expected)
    {
        long size = S3PartSizes.ForCopy(length);

        size.ShouldBe(expected);
        ((length + size - 1) / size).ShouldBeLessThanOrEqualTo(S3Limits.MaxParts);
    }
}
