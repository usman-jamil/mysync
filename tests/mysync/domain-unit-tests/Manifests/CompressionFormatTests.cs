using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Manifests;

/// <summary>
/// The compression setting and format 3 (CLAUDE.md §6): a share that has ever had compression on is
/// format 3 for good, so a client that can't decompress refuses it instead of failing on hashes.
/// </summary>
public sealed class CompressionFormatTests
{
    [Fact]
    public void AShareWithoutCompression_Should_StayFormatOne()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "a"));
        Manifest next = Ok(head.CreateNextRevision(head.Entries, Updated, "b"));

        foreach (Manifest manifest in new[] { head, next })
        {
            manifest.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);
            manifest.Compression.ShouldBe(CompressionSetting.None);
            manifest.RecordsCompression.ShouldBeFalse();
        }
    }

    [Fact]
    public void CreatingWithCompression_Should_MakeRevisionOneFormatThree()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "a", CompressionSetting.Gzip));

        head.Revision.ShouldBe(Rev(1));
        head.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        head.Compression.ShouldBe(CompressionSetting.Gzip);
    }

    [Fact]
    public void TurningCompressionOn_Should_CommitTheNextRevision_WithTheSameEntries_AsFormatThree()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "a"));

        Manifest on = Ok(head.WithCompression(CompressionSetting.Gzip, Updated, "b"));

        on.Revision.ShouldBe(Rev(2));
        on.Entries.ShouldBe(head.Entries);
        on.CreatedUtc.ShouldBe(head.CreatedUtc);
        on.UpdatedBy.ShouldBe("b");
        on.Compression.ShouldBe(CompressionSetting.Gzip);
        on.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
    }

    [Fact]
    public void TurningCompressionOff_Should_KeepFormatThree_ForGood()
    {
        Manifest on = Ok(Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "a")).WithCompression(CompressionSetting.Gzip, Updated, "a"));

        Manifest off = Ok(on.WithCompression(CompressionSetting.None, Updated, "a"));
        Manifest later = Ok(off.CreateNextRevision([Entry("b.txt", File())], Updated, "a"));

        foreach (Manifest manifest in new[] { off, later })
        {
            manifest.Compression.ShouldBe(CompressionSetting.None);
            manifest.RecordsCompression.ShouldBeTrue();
            manifest.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        }
    }

    [Theory]
    [InlineData(CompressionSetting.Gzip)]
    [InlineData(CompressionSetting.None)]
    public void EveryLaterRevision_Should_CarryTheSettingAndFormatForward(CompressionSetting setting)
    {
        Manifest head = Ok(Ok(Manifest.CreateNew(Share(), [], Created, "a", CompressionSetting.Gzip)).WithCompression(setting, Updated, "a"));

        Manifest manifest = head;
        for (int i = 0; i < 3; i++)
        {
            manifest = Ok(manifest.CreateNextRevision([Entry($"f{i}.txt", File())], Updated, "b"));
        }

        manifest.Compression.ShouldBe(setting);
        manifest.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
    }

    [Fact]
    public void DeletingACompressedShare_Should_WriteTheFormatTwoMarker()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "a", CompressionSetting.Gzip));

        Manifest marker = Ok(head.CreateDeletedRevision(Updated, "a"));

        marker.FormatVersion.ShouldBe(Manifest.DeletedFormatVersion);
        marker.RecordsCompression.ShouldBeFalse();
        marker.WithCompression(CompressionSetting.Gzip, Updated, "a").Error.Code.ShouldBe("Shares.Deleted");
    }

    [Fact]
    public void Restore_Should_NeedTheSetting_InFormatThree_AndRefuseItElsewhere()
    {
        Restore(3, CompressionSetting.Gzip).Value.Compression.ShouldBe(CompressionSetting.Gzip);
        Restore(3, CompressionSetting.None).Value.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        Restore(1, null).Value.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);

        Restore(3, null).Error.Code.ShouldBe("Shares.InvalidManifest");
        Restore(1, CompressionSetting.Gzip).Error.Code.ShouldBe("Shares.InvalidManifest");
        Restore(1, CompressionSetting.None).Error.Code.ShouldBe("Shares.InvalidManifest");
        Manifest.Restore(2, Share(), Rev(2), Created, Updated, "a", [], new ShareDeletion(Updated, "a"), CompressionSetting.None)
            .Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void AFutureFormat_Should_BeRefused_SoThatClientUpgrades()
    {
        Restore(4, CompressionSetting.Gzip).Error.Code.ShouldBe("Shares.UnsupportedFormat");
        Manifest.MaxReadableFormatVersion.ShouldBe(3);
    }

    [Fact]
    public void AReaderLimitedToFormatTwo_Should_RefuseACompressedShare()
    {
        // What 0.7 and older do: anything above their highest format is "upgrade mysync".
        Manifest compressed = Ok(Manifest.CreateNew(Share(), [], Created, "a", CompressionSetting.Gzip));

        (compressed.FormatVersion > Manifest.DeletedFormatVersion).ShouldBeTrue();
    }

    [Theory]
    [InlineData("gzip", true, CompressionSetting.Gzip)]
    [InlineData("none", true, CompressionSetting.None)]
    [InlineData("GZIP", false, CompressionSetting.None)]
    [InlineData("zstd", false, CompressionSetting.None)]
    [InlineData(null, false, CompressionSetting.None)]
    public void SettingCodes_Should_BeExact(string? code, bool parses, CompressionSetting expected)
    {
        CompressionSettings.TryParse(code, out CompressionSetting setting).ShouldBe(parses);
        setting.ShouldBe(expected);
        if (parses)
        {
            setting.ToCode().ShouldBe(code);
        }
    }

    private static SharedKernel.Result<Manifest> Restore(int format, CompressionSetting? compression) =>
        Manifest.Restore(format, Share(), Rev(5), Created, Updated, "a", [Entry("a.txt", File())], null, compression);
}
