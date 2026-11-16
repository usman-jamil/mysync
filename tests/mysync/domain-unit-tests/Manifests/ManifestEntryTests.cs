using MySync.Domain.Manifests;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Manifests;

public class ManifestEntryTests
{
    [Fact]
    public void Create_Should_RejectANegativeSize()
    {
        FileEntry.Create(Hash(HashOfTest), -1, Created, executable: false)
            .Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void Create_Should_StoreTheMtimeInUtc()
    {
        DateTimeOffset local = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(5));

        FileEntry file = Ok(FileEntry.Create(Hash(HashOfTest), 4, local, executable: false));

        file.MtimeUtc.Offset.ShouldBe(TimeSpan.Zero);
        file.MtimeUtc.ShouldBe(local);
    }

    [Fact]
    public void HaveSameContent_Should_TreatTwoAbsentEntriesAsTheSame()
    {
        ManifestEntry.HaveSameContent(null, null).ShouldBeTrue();
    }

    [Fact]
    public void HaveSameContent_Should_CompareFilesByHashOnly()
    {
        FileEntry original = File();
        FileEntry touched = Ok(FileEntry.Create(original.Sha256, original.Size, Updated, executable: true));

        ManifestEntry.HaveSameContent(original, touched).ShouldBeTrue();
        ManifestEntry.HaveSameContent(original, File(HashOfEmpty, size: 0)).ShouldBeFalse();
    }

    [Fact]
    public void HaveSameContent_Should_TreatTypeChangesAndDeletesAsDifferent()
    {
        ManifestEntry.HaveSameContent(File(), DirectoryEntry.Instance).ShouldBeFalse();
        ManifestEntry.HaveSameContent(File(), null).ShouldBeFalse();
        ManifestEntry.HaveSameContent(null, DirectoryEntry.Instance).ShouldBeFalse();
        ManifestEntry.HaveSameContent(DirectoryEntry.Instance, DirectoryEntry.Instance).ShouldBeTrue();
    }
}
