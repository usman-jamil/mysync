using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Manifests;

public class ManifestTests
{
    [Fact]
    public void CreateNew_Should_StartAtRevisionOne_WithCreatedEqualToUpdated()
    {
        Manifest manifest = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "usman@DEV-LAPTOP"));

        manifest.Revision.ShouldBe(Revision.Initial);
        manifest.CreatedUtc.ShouldBe(Created);
        manifest.UpdatedUtc.ShouldBe(Created);
        manifest.UpdatedBy.ShouldBe("usman@DEV-LAPTOP");
    }

    [Fact]
    public void CreateNextRevision_Should_AddOneAndKeepShareAndCreationTime()
    {
        Manifest first = Ok(Manifest.CreateNew(Share(), [], Created, "usman@DEV-LAPTOP"));

        Manifest second = Ok(first.CreateNextRevision([Entry("b.txt", File())], Updated, "ali@SERVER01"));

        second.Revision.ShouldBe(Rev(2));
        second.ShareId.ShouldBe(first.ShareId);
        second.CreatedUtc.ShouldBe(Created);
        second.UpdatedUtc.ShouldBe(Updated);
        second.UpdatedBy.ShouldBe("ali@SERVER01");
    }

    [Fact]
    public void Entries_Should_BeSortedOrdinally_WhateverTheInputOrder()
    {
        Manifest manifest = Ok(Manifest.CreateNew(
            Share(),
            [Entry("b", File()), Entry("a/z", File()), Entry("B", File()), Entry("a-b", File()), Entry("a/a", File())],
            Created,
            "x"));

        // Ordinal: uppercase before lowercase, '-' (0x2D) before '/' (0x2F).
        manifest.Entries.Keys.Select(path => path.Value).ShouldBe(["B", "a-b", "a/a", "a/z", "b"]);
    }

    [Fact]
    public void Build_Should_RejectTheSamePathTwice()
    {
        Manifest.CreateNew(Share(), [Entry("a.txt", File()), Entry("a.txt", File(HashOfEmpty, 0))], Created, "x")
            .Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void Build_Should_RejectNfdAndNfcSpellingsOfTheSameName_AsDuplicates()
    {
        // Both normalize to "café.txt", so on disk they are one file.
        Manifest.CreateNew(Share(), [Entry("café.txt", File()), Entry("café.txt", File())], Created, "x")
            .Error.Description.ShouldContain("more than once");
    }

    [Fact]
    public void Build_Should_RejectAnEntryInsideAFile()
    {
        Manifest.CreateNew(Share(), [Entry("docs", File()), Entry("docs/readme.md", File())], Created, "x")
            .Error.Description.ShouldContain("which is a file");
    }

    [Fact]
    public void Build_Should_RejectAnEntryDeeplyInsideAFile()
    {
        Manifest.CreateNew(Share(), [Entry("a", File()), Entry("a/b/c/d.txt", File())], Created, "x")
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Build_Should_DropDirectoryEntriesThatAreNotEmpty()
    {
        Manifest manifest = Ok(Manifest.CreateNew(
            Share(),
            [Entry("logs", DirectoryEntry.Instance), Entry("logs/today.log", File()), Entry("empty", DirectoryEntry.Instance)],
            Created,
            "x"));

        manifest.Entries.Keys.Select(path => path.Value).ShouldBe(["empty", "logs/today.log"]);
    }

    [Fact]
    public void Build_Should_NotTreatASiblingWithACommonPrefixAsAChild()
    {
        // "a.txt" is a file; "a.txt2/b" is not inside it.
        Manifest.CreateNew(Share(), [Entry("a.txt", File()), Entry("a.txt2/b", File())], Created, "x")
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void FileCountAndTotalSize_Should_CountFilesOnly()
    {
        Manifest manifest = Ok(Manifest.CreateNew(
            Share(),
            [Entry("a", File(size: 10)), Entry("b/c", File(HashOfEmpty, size: 0)), Entry("d", DirectoryEntry.Instance)],
            Created,
            "x"));

        manifest.FileCount.ShouldBe(2);
        manifest.TotalSize.ShouldBe(10);
    }

    [Fact]
    public void Restore_Should_RefuseANewerFormat_AndTellTheUserToUpgrade()
    {
        Error error = Manifest.Restore(Manifest.MaxReadableFormatVersion + 1, Share(), Rev(17), Created, Updated, "x", []).Error;

        error.ShouldBe(ShareErrors.UnsupportedFormat(Manifest.MaxReadableFormatVersion + 1));
        error.Description.ShouldContain("Upgrade mysync");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Restore_Should_RejectAnInvalidFormatVersion(int formatVersion)
    {
        Manifest.Restore(formatVersion, Share(), Rev(17), Created, Updated, "x", [])
            .Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void Restore_Should_KeepTheStoredValues()
    {
        Manifest manifest = Ok(Manifest.Restore(1, Share(), Rev(17), Created, Updated, "ali@SERVER01", [Entry("a", File())]));

        manifest.Revision.ShouldBe(Rev(17));
        manifest.CreatedUtc.ShouldBe(Created);
        manifest.UpdatedUtc.ShouldBe(Updated);
        manifest.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void Restore_Should_AcceptAnUpdateTimeBeforeTheCreationTime_BecauseClocksDiffer()
    {
        Manifest.Restore(1, Share(), Rev(2), Updated, Created, "x", []).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Restore_Should_ApplyTheSameEntryRulesAsCreate()
    {
        Manifest.Restore(1, Share(), Rev(3), Created, Updated, "x", [Entry("a", File()), Entry("a/b", File())])
            .Error.Code.ShouldBe("Shares.InvalidManifest");
    }
}
