using MySync.Application.Abstractions.Storage;
using System.Text;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Storage;

/// <summary>What the fake adds on top of the contract, for handler tests to inspect.</summary>
public class InMemoryShareStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsReady_Should_RecordThatTheCreateFlowPreparedTheStore()
    {
        InMemoryShareStore store = new();
        store.IsReady.ShouldBeFalse();

        await store.EnsureReadyForNewShareAsync(Ct);

        store.IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task BlobNames_Should_UseTheRealLayout()
    {
        InMemoryShareStore store = new();
        ManifestBuilder builder = new ManifestBuilder().WithFile("a.txt", "a");
        byte[] content = Encoding.UTF8.GetBytes("a");
        var layout = ShareLayout.For(builder.ShareId);

        await store.CreateManifestAsync(builder.ShareId, builder.Build(), Ct);
        await store.PutManifestHistoryAsync(builder.ShareId, builder.Build(), Ct);
        await store.UploadObjectAsync(builder.ShareId, ManifestBuilder.HashOf(content), ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct);

        store.BlobNames.ShouldBe(
            [layout.Manifest, layout.ManifestHistory(Domain.Manifests.Revision.Initial), layout.ObjectBlob(ManifestBuilder.HashOf(content))],
            ignoreOrder: true);
    }

    [Fact]
    public async Task ListObjects_Should_SkipBlobsThatAreNotCanonicalObjectNames()
    {
        InMemoryShareStore store = new();
        var shareId = ShareId.New();
        store.PutStrayBlob(ShareLayout.For(shareId).ObjectsPrefix + "zz/not-a-hash", [1]);

        int count = 0;
        await foreach (SharedKernel.Result<Application.Abstractions.Storage.StoredObjectInfo> unused in store.ListObjectsAsync(shareId, Ct))
        {
            count++;
        }

        count.ShouldBe(0);
    }

    [Fact]
    public async Task LastModified_Should_ComeFromTheInjectedClock_SoGcGracePeriodsCanBeTested()
    {
        FixedDateTimeProvider clock = new(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));
        InMemoryShareStore store = new(clock);
        var shareId = ShareId.New();
        byte[] content = [1, 2, 3];

        await store.UploadObjectAsync(shareId, ManifestBuilder.HashOf(content), ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct);

        await foreach (SharedKernel.Result<Application.Abstractions.Storage.StoredObjectInfo> item in store.ListObjectsAsync(shareId, Ct))
        {
            item.Value.LastModifiedUtc.ShouldBe(new DateTimeOffset(clock.UtcNow, TimeSpan.Zero));
        }
    }
}
