using System.Text;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Testing.Contracts;

/// <summary>
/// The behaviour every <see cref="IShareStore"/> must have (CLAUDE.md §11, §14). Derive once per
/// implementation: <see cref="InMemoryShareStore"/> in the application unit tests, Azure Blob
/// against Azurite in the storage integration tests. If the fake and Azurite disagree, the fake
/// is wrong.
/// </summary>
/// <remarks>
/// Timestamps are whole seconds: the manifest format's precision belongs to the serializer, not
/// to this contract.
/// </remarks>
public abstract class ShareStoreContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh, isolated, empty store (its own container), not yet made ready.</summary>
    protected abstract Task<IShareStore> CreateStoreAsync();

    [Fact]
    public async Task GetLocation_Should_NameProviderAccountAndContainer()
    {
        IShareStore store = await CreateStoreAsync();

        Result<Domain.State.StorageLocation> location = store.GetLocation();

        location.IsSuccess.ShouldBeTrue();
        location.Value.Provider.ShouldNotBeNullOrWhiteSpace();
        location.Value.AccountName.ShouldNotBeNullOrWhiteSpace();
        location.Value.Container.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task EnsureReadyForNewShare_Should_BeIdempotent()
    {
        IShareStore store = await CreateStoreAsync();

        (await store.EnsureReadyForNewShareAsync(Ct)).IsSuccess.ShouldBeTrue();
        (await store.EnsureReadyForNewShareAsync(Ct)).IsSuccess.ShouldBeTrue();
    }

    // Manifest HEAD

    [Fact]
    public async Task GetManifest_Should_ReturnNull_BeforeTheStoreIsReady()
    {
        IShareStore store = await CreateStoreAsync();

        Result<ManifestSnapshot?> result = await store.GetManifestAsync(ShareId.New(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task GetManifest_Should_ReturnNull_ForAShareThatDoesNotExist()
    {
        IShareStore store = await ReadyStoreAsync();

        (await store.GetManifestAsync(ShareId.New(), Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task CreateManifest_Should_StoreTheManifest_AndReturnTheETagAGetSees()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().WithFile("a.txt", "a").Build();

        string etag = Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));
        ManifestSnapshot snapshot = (await store.GetManifestAsync(manifest.ShareId, Ct)).Value!;

        etag.ShouldNotBeNullOrWhiteSpace();
        snapshot.ETag.ShouldBe(etag);
        snapshot.Manifest.ShouldMatchManifest(manifest);
    }

    [Fact]
    public async Task CreateManifest_Should_FailWithAlreadyExists_AndLeaveTheExistingOneAlone()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new ManifestBuilder().WithFile("a.txt", "first");
        Manifest first = builder.Build();
        Unwrap(await store.CreateManifestAsync(first.ShareId, first, Ct));

        Result<string> second = await store.CreateManifestAsync(first.ShareId, builder.WithFile("a.txt", "second").Build(), Ct);

        second.Error.Code.ShouldBe("Storage.AlreadyExists");
        (await store.GetManifestAsync(first.ShareId, Ct)).Value!.Manifest.ShouldMatchManifest(first);
    }

    [Fact]
    public async Task ReplaceManifest_Should_Succeed_WithTheCurrentETag_AndReturnANewOne()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new ManifestBuilder().WithFile("a.txt", "v1");
        string firstETag = Unwrap(await store.CreateManifestAsync(builder.ShareId, builder.Build(), Ct));
        Manifest second = builder.WithFile("a.txt", "v2").Build(revision: 2);

        string secondETag = Unwrap(await store.ReplaceManifestAsync(builder.ShareId, second, firstETag, Ct));
        ManifestSnapshot snapshot = (await store.GetManifestAsync(builder.ShareId, Ct)).Value!;

        secondETag.ShouldNotBe(firstETag);
        snapshot.ETag.ShouldBe(secondETag);
        snapshot.Manifest.ShouldMatchManifest(second);
    }

    [Fact]
    public async Task ReplaceManifest_Should_GiveANewETag_EvenForIdenticalContent()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().WithFile("a.txt", "a").Build();
        string first = Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));

        string second = Unwrap(await store.ReplaceManifestAsync(manifest.ShareId, manifest, first, Ct));

        second.ShouldNotBe(first);
        (await store.ReplaceManifestAsync(manifest.ShareId, manifest, first, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
    }

    [Fact]
    public async Task ReplaceManifest_Should_FailWithPreconditionFailed_ForAStaleETag_AndChangeNothing()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new ManifestBuilder().WithFile("a.txt", "v1");
        string staleETag = Unwrap(await store.CreateManifestAsync(builder.ShareId, builder.Build(), Ct));
        Manifest theirs = builder.WithFile("a.txt", "theirs").Build(revision: 2);
        Unwrap(await store.ReplaceManifestAsync(builder.ShareId, theirs, staleETag, Ct));

        Result<string> ours = await store.ReplaceManifestAsync(builder.ShareId, builder.WithFile("a.txt", "ours").Build(revision: 2), staleETag, Ct);

        ours.Error.Code.ShouldBe("Storage.PreconditionFailed");
        (await store.GetManifestAsync(builder.ShareId, Ct)).Value!.Manifest.ShouldMatchManifest(theirs);
    }

    [Fact]
    public async Task ReplaceManifest_Should_FailWithPreconditionFailed_WhenThereIsNoManifest()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().Build(revision: 2);

        (await store.ReplaceManifestAsync(manifest.ShareId, manifest, "\"0x1\"", Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
        (await store.GetManifestAsync(manifest.ShareId, Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentReplaces_WithTheSameETag_Should_LetExactlyOneWin()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new();
        string etag = Unwrap(await store.CreateManifestAsync(builder.ShareId, builder.Build(), Ct));

        Result<string>[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.ReplaceManifestAsync(builder.ShareId, new ManifestBuilder(builder.ShareId).WithFile($"from-{i}", "x").Build(revision: 2), etag, Ct)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailure).ShouldAllBe(result => result.Error.Code == "Storage.PreconditionFailed");
    }

    [Fact]
    public async Task ConcurrentCreates_Should_LetExactlyOneWin()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();

        Result<string>[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.CreateManifestAsync(shareId, new ManifestBuilder(shareId).WithFile($"from-{i}", "x").Build(), Ct)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailure).ShouldAllBe(result => result.Error.Code == "Storage.AlreadyExists");
    }

    [Fact]
    public async Task Manifests_Should_BeScopedToTheirShare()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().Build();
        Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));

        (await store.GetManifestAsync(ShareId.New(), Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Manifest_Should_RoundTripEveryKindOfEntry()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder()
            .WithFile("docs/readme.md", "# readme")
            .WithFile("scripts/deploy.sh", "#!/bin/sh", executable: true)
            .WithFile("empty.txt", "")
            .WithFile("café/日本.txt", "unicode")
            .WithDirectory("logs")
            .Build(revision: 1, updatedBy: "usman@DEV-LAPTOP", updatedUtc: new DateTimeOffset(2026, 11, 16, 9, 2, 55, TimeSpan.Zero));

        Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));

        (await store.GetManifestAsync(manifest.ShareId, Ct)).Value!.Manifest.ShouldMatchManifest(manifest);
    }

    // Objects

    [Fact]
    public async Task UploadedObject_Should_ReadBackByteForByte()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = new byte[1024 * 1024 + 7];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content);
        ContentHash hash = ManifestBuilder.HashOf(content);

        Unwrap(await Upload(store, shareId, hash, content));

        (await ReadAll(store, shareId, hash)).ShouldBe(content);
    }

    [Fact]
    public async Task UploadedObject_Should_AllowAnEmptyFile()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf([]);

        Unwrap(await Upload(store, shareId, hash, []));

        (await ReadAll(store, shareId, hash)).ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadObject_Should_Succeed_WhenTheObjectAlreadyExists()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("same bytes");
        ContentHash hash = ManifestBuilder.HashOf(content);
        Unwrap(await Upload(store, shareId, hash, content));

        (await Upload(store, shareId, hash, content)).IsSuccess.ShouldBeTrue();
        (await ReadAll(store, shareId, hash)).ShouldBe(content);
    }

    [Fact]
    public async Task UploadObject_Should_RejectBytesThatDoNotMatchTheHash_AndStoreNothing()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        ContentHash claimed = ManifestBuilder.HashOf("what the scan saw");

        Result result = await Upload(store, shareId, claimed, Encoding.UTF8.GetBytes("what the file holds now"));

        result.Error.Code.ShouldBe("Storage.HashMismatch");
        Unwrap(await store.ObjectExistsAsync(shareId, claimed, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ObjectExists_Should_FollowUploadsAndDeletes()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("x");
        ContentHash hash = ManifestBuilder.HashOf(content);

        Unwrap(await store.ObjectExistsAsync(shareId, hash, Ct)).ShouldBeFalse();
        Unwrap(await Upload(store, shareId, hash, content));
        Unwrap(await store.ObjectExistsAsync(shareId, hash, Ct)).ShouldBeTrue();
        Unwrap(await store.DeleteObjectAsync(shareId, hash, Ct));
        Unwrap(await store.ObjectExistsAsync(shareId, hash, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenObjectRead_Should_FailWithNotFound_ForAMissingObject()
    {
        IShareStore store = await ReadyStoreAsync();

        (await store.OpenObjectReadAsync(ShareId.New(), ManifestBuilder.HashOf("missing"), Ct)).Error.Code.ShouldBe("Storage.NotFound");
    }

    [Fact]
    public async Task DeleteObject_Should_Succeed_ForAMissingObject()
    {
        IShareStore store = await ReadyStoreAsync();

        (await store.DeleteObjectAsync(ShareId.New(), ManifestBuilder.HashOf("missing"), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Objects_Should_BeScopedToTheirShare()
    {
        IShareStore store = await ReadyStoreAsync();
        byte[] content = Encoding.UTF8.GetBytes("x");
        ContentHash hash = ManifestBuilder.HashOf(content);
        Unwrap(await Upload(store, ShareId.New(), hash, content));

        Unwrap(await store.ObjectExistsAsync(ShareId.New(), hash, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ListObjects_Should_ListEachObjectOfTheShareOnce_WithItsSize()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[][] contents = [Encoding.UTF8.GetBytes("one"), Encoding.UTF8.GetBytes("three"), []];
        foreach (byte[] content in contents)
        {
            Unwrap(await Upload(store, shareId, ManifestBuilder.HashOf(content), content));
        }

        byte[] other = Encoding.UTF8.GetBytes("other share");
        Unwrap(await Upload(store, ShareId.New(), ManifestBuilder.HashOf(other), other));
        Manifest manifest = new ManifestBuilder(shareId).Build();
        Unwrap(await store.CreateManifestAsync(shareId, manifest, Ct));
        Unwrap(await store.PutManifestHistoryAsync(shareId, manifest, Ct));

        List<StoredObjectInfo> listed = [];
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(shareId, Ct))
        {
            listed.Add(Unwrap(item));
        }

        listed.Select(info => (info.Hash.Value, info.Size)).Order().ShouldBe(
            contents.Select(content => (ManifestBuilder.HashOf(content).Value, content.LongLength)).Order());
        listed.ShouldAllBe(info => info.LastModifiedUtc > DateTimeOffset.MinValue);
    }

    // Manifest history

    [Fact]
    public async Task ManifestHistory_Should_BeEmpty_ForANewShare()
    {
        IShareStore store = await ReadyStoreAsync();

        (await ListHistory(store, ShareId.New())).ShouldBeEmpty();
    }

    [Fact]
    public async Task ManifestHistory_Should_ListOldestFirst_WhateverTheWriteOrder()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new ManifestBuilder().WithFile("a", "a");
        long[] revisions = [10, 2, 9, 1];

        foreach (long revision in revisions)
        {
            Unwrap(await store.PutManifestHistoryAsync(builder.ShareId, builder.Build(revision), Ct));
        }

        (await ListHistory(store, builder.ShareId)).Select(manifest => manifest.Revision.Value).ShouldBe([1, 2, 9, 10]);
    }

    [Fact]
    public async Task PutManifestHistory_Should_Succeed_ForARevisionAlreadyStored()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().Build(revision: 3);
        Unwrap(await store.PutManifestHistoryAsync(manifest.ShareId, manifest, Ct));

        (await store.PutManifestHistoryAsync(manifest.ShareId, manifest, Ct)).IsSuccess.ShouldBeTrue();
        (await ListHistory(store, manifest.ShareId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteManifestHistory_Should_RemoveOneRevision_AndSucceedForAMissingOne()
    {
        IShareStore store = await ReadyStoreAsync();
        ManifestBuilder builder = new();
        Unwrap(await store.PutManifestHistoryAsync(builder.ShareId, builder.Build(1), Ct));
        Unwrap(await store.PutManifestHistoryAsync(builder.ShareId, builder.Build(2), Ct));

        Unwrap(await store.DeleteManifestHistoryAsync(builder.ShareId, Revision.Initial, Ct));

        (await store.DeleteManifestHistoryAsync(builder.ShareId, Revision.Initial, Ct)).IsSuccess.ShouldBeTrue();
        (await ListHistory(store, builder.ShareId)).Select(manifest => manifest.Revision.Value).ShouldBe([2]);
    }

    [Fact]
    public async Task ManifestHistory_Should_NotChangeHead()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().Build(revision: 5);

        Unwrap(await store.PutManifestHistoryAsync(manifest.ShareId, manifest, Ct));

        (await store.GetManifestAsync(manifest.ShareId, Ct)).Value.ShouldBeNull();
    }

    private async Task<IShareStore> ReadyStoreAsync()
    {
        IShareStore store = await CreateStoreAsync();
        Unwrap(await store.EnsureReadyForNewShareAsync(Ct));
        return store;
    }

    private static async Task<Result> Upload(IShareStore store, ShareId shareId, ContentHash hash, byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        return await store.UploadObjectAsync(shareId, hash, stream, content.LongLength, Ct);
    }

    private static async Task<byte[]> ReadAll(IShareStore store, ShareId shareId, ContentHash hash)
    {
        await using Stream stream = Unwrap(await store.OpenObjectReadAsync(shareId, hash, Ct));
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private static async Task<List<Manifest>> ListHistory(IShareStore store, ShareId shareId)
    {
        List<Manifest> history = [];
        await foreach (Result<Manifest> item in store.ListManifestHistoryAsync(shareId, Ct))
        {
            history.Add(Unwrap(item));
        }

        return history;
    }

    private static void Unwrap(Result result) =>
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);

    private static T Unwrap<T>(Result<T> result)
    {
        Unwrap((Result)result);
        return result.Value;
    }
}
