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
    public void AreEquivalent_Should_TreatTwoAbsentEntriesAsTheSame()
    {
        ManifestEntry.AreEquivalent(null, null).ShouldBeTrue();
    }

    [Fact]
    public void AreEquivalent_Should_IgnoreTheMtime_SoTouchingAFileIsNotAChange()
    {
        FileEntry original = File();
        FileEntry touched = Ok(FileEntry.Create(original.Sha256, original.Size, Updated, original.Executable));

        ManifestEntry.AreEquivalent(original, touched).ShouldBeTrue();
    }

    [Fact]
    public void AreEquivalent_Should_TreatADifferentHashAsAChange()
    {
        ManifestEntry.AreEquivalent(File(), File(HashOfEmpty, size: 0)).ShouldBeFalse();
    }

    [Fact]
    public void AreEquivalent_Should_TreatAnExecutableBitChangeAsAChange_SoChmodSyncs()
    {
        FileEntry script = File(executable: false);
        FileEntry madeExecutable = Ok(FileEntry.Create(script.Sha256, script.Size, script.MtimeUtc, executable: true));

        ManifestEntry.AreEquivalent(script, madeExecutable).ShouldBeFalse();
        ManifestEntry.AreEquivalent(madeExecutable, script).ShouldBeFalse();
    }

    [Fact]
    public void AreEquivalent_Should_TreatTypeChangesAndDeletesAsDifferent()
    {
        ManifestEntry.AreEquivalent(File(), DirectoryEntry.Instance).ShouldBeFalse();
        ManifestEntry.AreEquivalent(File(), null).ShouldBeFalse();
        ManifestEntry.AreEquivalent(null, DirectoryEntry.Instance).ShouldBeFalse();
        ManifestEntry.AreEquivalent(DirectoryEntry.Instance, DirectoryEntry.Instance).ShouldBeTrue();
    }
}
