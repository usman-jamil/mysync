using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.State;

public class ShareStateTests
{
    private static readonly StorageLocation Recorded = new("azure-blob", "teamsyncstorage", "mysync");

    [Fact]
    public void FromManifest_Should_UseTheManifestAsBase_WithNothingCachedAndNoConflicts()
    {
        Manifest manifest = Ok(Manifest.CreateNew(Share(), [Entry("a.txt", File())], Created, "x"));

        var state = ShareState.FromManifest(Recorded, manifest);

        state.ShareId.ShouldBe(manifest.ShareId);
        state.BaseRevision.ShouldBe(manifest.Revision);
        state.Base.ShouldBe(manifest.Entries);
        state.HashCache.ShouldBeEmpty();
        state.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void Restore_Should_RefuseANewerFormat()
    {
        Restore(formatVersion: 2).Error.ShouldBe(StateErrors.UnsupportedFormat(2));
    }

    [Fact]
    public void Restore_Should_TreatAnInvalidFormatVersionAsCorrupt_NeverAsANewShare()
    {
        Restore(formatVersion: 0).Error.Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public void Restore_Should_TreatTheSameBasePathTwiceAsCorrupt()
    {
        Restore(baseEntries: [Entry("a", File()), Entry("a", File(HashOfEmpty, 0))]).Error.Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public void Restore_Should_TreatTwoConflictsForOnePathAsCorrupt()
    {
        Restore(conflicts: [Conflict("a"), Conflict("a")]).Error.Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public void Restore_Should_SortConflictsByPath()
    {
        ShareState state = Ok(Restore(conflicts: [Conflict("b"), Conflict("a")]));

        state.Conflicts.Select(conflict => conflict.Path.Value).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Restore_Should_KeepTheLastHashCacheEntry_WhenAPathRepeats()
    {
        HashCacheEntry first = new(4, 100, Hash(HashOfTest));
        HashCacheEntry second = new(0, 200, Hash(HashOfEmpty));

        ShareState state = Ok(Restore(hashCache: [new(Path("a"), first), new(Path("a"), second)]));

        state.HashCache[Path("a")].ShouldBe(second);
    }

    [Fact]
    public void CheckLocation_Should_Pass_ForTheSameLocation_IgnoringCaseOfAccountAndContainer()
    {
        ShareState state = Ok(Restore());

        state.CheckLocation(new StorageLocation("azure-blob", "TeamSyncStorage", "MySync")).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("azure-blob", "otheraccount", "mysync")]
    [InlineData("azure-blob", "teamsyncstorage", "other")]
    [InlineData("s3", "teamsyncstorage", "mysync")]
    public void CheckLocation_Should_Fail_WhenTheEnvironmentPointsElsewhere(string provider, string account, string container)
    {
        Error error = Ok(Restore()).CheckLocation(new StorageLocation(provider, account, container)).Error;

        error.Code.ShouldBe("State.StorageMismatch");
        error.Description.ShouldContain("teamsyncstorage");
    }

    [Fact]
    public void HashCacheEntry_Should_MatchOnlyWhenSizeAndMtimeBothMatch()
    {
        HashCacheEntry entry = new(4, 100, Hash(HashOfTest));

        entry.Matches(4, 100).ShouldBeTrue();
        entry.Matches(5, 100).ShouldBeFalse();
        entry.Matches(4, 101).ShouldBeFalse();
    }

    private static Conflict Conflict(string path) =>
        new(Path(path), ConflictKind.ModifyModify, File(), File(HashOfEmpty, 0), File(), Rev(18), "ali@SERVER01", Updated, Updated);

    private static Result<ShareState> Restore(
        int formatVersion = 1,
        KeyValuePair<SharePath, ManifestEntry>[]? baseEntries = null,
        KeyValuePair<SharePath, HashCacheEntry>[]? hashCache = null,
        Conflict[]? conflicts = null) =>
        ShareState.Restore(formatVersion, Recorded, Share(), Rev(17), baseEntries ?? [], hashCache ?? [], conflicts ?? []);
}
