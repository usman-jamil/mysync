using System.Text;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
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

    /// <summary>
    /// Writes <paramref name="bytes"/> as an object of <paramref name="store"/> with exactly this
    /// <c>Content-Encoding</c>, bypassing the store's own checks: what a newer client or a crafted
    /// share could leave behind.
    /// </summary>
    protected abstract Task PlantObjectAsync(IShareStore store, ShareId shareId, ContentHash hash, byte[] bytes, string contentEncoding);

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
    public async Task ARawObject_Should_ReadBackAsIdentity_WithItsLength()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("plain bytes");
        ContentHash hash = ManifestBuilder.HashOf(content);
        Unwrap(await Upload(store, shareId, hash, content));

        await using StoredObject stored = Unwrap(await store.OpenObjectReadAsync(shareId, hash, Ct));

        stored.Encoding.ShouldBe(ObjectEncoding.Identity);
        stored.StoredLength.ShouldBe(content.LongLength);
    }

    [Fact]
    public async Task AGzipObject_Should_ReadBackAsStored_NotDecoded()
    {
        // Pins what the provider's SDK does with Content-Encoding: gzip. The bytes must come back
        // exactly as stored, so that mysync decodes them exactly once.
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] original = TextOf(400_000); // well over the test block size: several blocks
        ContentHash hash = ManifestBuilder.HashOf(original);
        byte[] compressed = Gzip(original);

        Unwrap(await UploadGzip(store, shareId, hash, compressed, original.LongLength));

        await using StoredObject stored = Unwrap(await store.OpenObjectReadAsync(shareId, hash, Ct));
        stored.Encoding.ShouldBe(ObjectEncoding.Gzip);
        stored.StoredLength.ShouldBe(compressed.LongLength);
        byte[] read = await ReadToEnd(stored.Content);
        read.ShouldBe(compressed);
        Gunzip(read).ShouldBe(original);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFirstForm_Should_Win_AndTheOtherBeAlreadyStored(bool rawFirst)
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] original = TextOf(20_000);
        ContentHash hash = ManifestBuilder.HashOf(original);
        byte[] compressed = Gzip(original);

        Result first = rawFirst ? await Upload(store, shareId, hash, original) : await UploadGzip(store, shareId, hash, compressed, original.LongLength);
        Result second = rawFirst ? await UploadGzip(store, shareId, hash, compressed, original.LongLength) : await Upload(store, shareId, hash, original);

        Unwrap(first);
        Unwrap(second);
        await using StoredObject stored = Unwrap(await store.OpenObjectReadAsync(shareId, hash, Ct));
        stored.Encoding.ShouldBe(rawFirst ? ObjectEncoding.Identity : ObjectEncoding.Gzip);
        (await ReadToEnd(stored.Content)).ShouldBe(rawFirst ? original : compressed);
    }

    [Fact]
    public async Task AFailedVerdict_Should_StoreNothing_AndBeAskedOnlyAfterTheLastByte()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] original = TextOf(300_000);
        ContentHash hash = ManifestBuilder.HashOf(original);
        using EndTrackingStream content = new(Gzip(original));
        bool askedAfterTheEnd = false;

        Result result = await store.UploadObjectAsync(
            shareId,
            hash,
            new ObjectUpload(content, ObjectEncoding.Gzip, original.LongLength, () =>
            {
                askedAfterTheEnd = content.ReachedEnd;
                return Result.Failure(Domain.Storage.StorageErrors.HashMismatch("the file changed"));
            }),
            Ct);

        result.Error.Code.ShouldBe("Storage.HashMismatch");
        askedAfterTheEnd.ShouldBeTrue();
        Unwrap(await store.ObjectExistsAsync(shareId, hash, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnUpload_Should_NeedNeitherALengthNorASeekableStream()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] original = TextOf(250_000);
        ContentHash hash = ManifestBuilder.HashOf(original);
        using EndTrackingStream content = new(Gzip(original));

        Unwrap(await store.UploadObjectAsync(shareId, hash, new ObjectUpload(content, ObjectEncoding.Gzip, original.LongLength, () => Result.Success()), Ct));

        Unwrap(await store.ObjectExistsAsync(shareId, hash, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnknownEncoding_Should_BeUnsupported_WhenReadOrListed()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] bytes = Encoding.UTF8.GetBytes("brotli, perhaps");
        ContentHash hash = ManifestBuilder.HashOf(bytes);
        await PlantObjectAsync(store, shareId, hash, bytes, "br");

        (await store.OpenObjectReadAsync(shareId, hash, Ct)).Error.Code.ShouldBe("Storage.UnsupportedEncoding");
        List<Result<StoredObjectInfo>> listed = [];
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(shareId, Ct))
        {
            listed.Add(item);
        }

        listed.ShouldContain(item => item.IsFailure && item.Error.Code == "Storage.UnsupportedEncoding");
    }

    [Fact]
    public async Task ListObjects_Should_ReportEachObjectsEncoding_AndStoredLength()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] raw = Encoding.UTF8.GetBytes("raw");
        byte[] original = TextOf(10_000);
        byte[] compressed = Gzip(original);
        Unwrap(await Upload(store, shareId, ManifestBuilder.HashOf(raw), raw));
        Unwrap(await UploadGzip(store, shareId, ManifestBuilder.HashOf(original), compressed, original.LongLength));

        List<StoredObjectInfo> listed = [];
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(shareId, Ct))
        {
            listed.Add(Unwrap(item));
        }

        listed.Single(info => info.Hash == ManifestBuilder.HashOf(raw)).ShouldSatisfyAllConditions(
            info => info.Encoding.ShouldBe(ObjectEncoding.Identity),
            info => info.Size.ShouldBe(raw.LongLength));
        listed.Single(info => info.Hash == ManifestBuilder.HashOf(original)).ShouldSatisfyAllConditions(
            info => info.Encoding.ShouldBe(ObjectEncoding.Gzip),
            info => info.Size.ShouldBe(compressed.LongLength));
    }

    [Fact]
    public async Task OpenObjectRead_Should_FailWithNotFound_ForAMissingObject()
    {
        IShareStore store = await ReadyStoreAsync();

        (await store.OpenObjectReadAsync(ShareId.New(), ManifestBuilder.HashOf("missing"), Ct)).Error.Code.ShouldBe("Storage.NotFound");
    }

    [Fact]
    public async Task ADeletedMarker_Should_RoundTrip_AsHead()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().WithFile("a.txt", "a").Build();
        string etag = Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));
        Manifest marker = Unwrap(manifest.CreateDeletedRevision(DateTimeOffset.UnixEpoch.AddYears(57), "deleter@HOST"));

        Unwrap(await store.ReplaceManifestAsync(manifest.ShareId, marker, etag, Ct));

        Manifest head = Unwrap(await store.GetManifestAsync(manifest.ShareId, Ct))!.Manifest;
        head.IsDeleted.ShouldBeTrue();
        head.Deletion.ShouldBe(marker.Deletion);
        head.Revision.ShouldBe(marker.Revision);
        head.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task CountRecoverableCopies_Should_BeZero_WhenTheStoreKeepsNone()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("gone");
        ContentHash hash = ManifestBuilder.HashOf(content);
        Unwrap(await Upload(store, shareId, hash, content));
        Unwrap(await store.DeleteObjectAsync(shareId, hash, Ct));

        Unwrap(await store.CountRecoverableCopiesAsync(shareId, Ct)).ShouldBe(0);
        Unwrap(await store.CountRecoverableCopiesAsync(ShareId.New(), Ct)).ShouldBe(0);
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

    [Fact]
    public async Task Publications_Should_BeNull_ForAShareThatNeverPublished()
    {
        IShareStore store = await ReadyStoreAsync();

        (await store.GetPublicationsAsync(ShareId.New(), Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Publications_Should_BeCreatedOnlyIfAbsent_ThenReplacedOnlyWithTheCurrentETag()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        PublicationRegistry one = PublicationRegistry.Empty.With(Record("AAAAAAAAAAAAAAAAAAAAAA"));
        PublicationRegistry two = one.With(Record("BBBBBBBBBBBBBBBBBBBBBB"));

        string first = Unwrap(await store.PutPublicationsAsync(shareId, one, ifMatchETag: null, Ct));
        (await store.PutPublicationsAsync(shareId, two, ifMatchETag: null, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");

        PublicationsSnapshot read = Unwrap(await store.GetPublicationsAsync(shareId, Ct))!;
        read.ETag.ShouldBe(first);
        read.Registry.Publications.ShouldBe(one.Publications);

        string second = Unwrap(await store.PutPublicationsAsync(shareId, two, first, Ct));
        second.ShouldNotBe(first);
        (await store.PutPublicationsAsync(shareId, one, first, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
        Unwrap(await store.GetPublicationsAsync(shareId, Ct))!.Registry.Publications.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeletePublications_Should_RemoveThem_AndSucceedWhenMissing()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        Unwrap(await store.PutPublicationsAsync(shareId, PublicationRegistry.Empty.With(Record("AAAAAAAAAAAAAAAAAAAAAA")), null, Ct));

        Unwrap(await store.DeletePublicationsAsync(shareId, Ct));

        (await store.GetPublicationsAsync(shareId, Ct)).Value.ShouldBeNull();
        (await store.DeletePublicationsAsync(shareId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Publications_Should_NotChangeHead_OrTheObjects()
    {
        IShareStore store = await ReadyStoreAsync();
        Manifest manifest = new ManifestBuilder().Build();
        Unwrap(await store.CreateManifestAsync(manifest.ShareId, manifest, Ct));

        Unwrap(await store.PutPublicationsAsync(manifest.ShareId, PublicationRegistry.Empty.With(Record("AAAAAAAAAAAAAAAAAAAAAA")), null, Ct));

        Unwrap(await store.GetManifestAsync(manifest.ShareId, Ct))!.Manifest.Revision.ShouldBe(manifest.Revision);
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(manifest.ShareId, Ct))
        {
            item.IsSuccess.ShouldBeFalse("publications.json is not an object");
        }
    }

    [Fact]
    public async Task GetObjectReadUrl_Should_GiveAUrl_OrNull_ButNeverFail()
    {
        IShareStore store = await ReadyStoreAsync();
        var shareId = ShareId.New();
        byte[] content = Encoding.UTF8.GetBytes("published content");
        ContentHash hash = ManifestBuilder.HashOf(content);
        Unwrap(await Upload(store, shareId, hash, content));

        Uri? url = Unwrap(await store.GetObjectReadUrlAsync(shareId, hash, TimeSpan.FromMinutes(5), Ct));

        if (url is not null)
        {
            url.IsAbsoluteUri.ShouldBeTrue();
            url.AbsoluteUri.ShouldNotContain("AccountKey", Case.Insensitive);
        }
    }

    private static Publication Record(string id) => new(
        PublishId.TryParse(id, out PublishId? parsed) ? parsed : throw new ArgumentException(id),
        PublicationStatus.Published, "title", Revision.Initial, 1, 2,
        new DateTimeOffset(2026, 11, 18, 7, 30, 0, TimeSpan.Zero), "me@HOST", "publishacct", $"https://x/p/{id}/", "sha256:0000");

    private async Task<IShareStore> ReadyStoreAsync()
    {
        IShareStore store = await CreateStoreAsync();
        Unwrap(await store.EnsureReadyForNewShareAsync(Ct));
        return store;
    }

    private static async Task<Result> Upload(IShareStore store, ShareId shareId, ContentHash hash, byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        return await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(stream, content.LongLength), Ct);
    }

    private static async Task<Result> UploadGzip(IShareStore store, ShareId shareId, ContentHash hash, byte[] compressed, long size)
    {
        using MemoryStream stream = new(compressed, writable: false);
        return await store.UploadObjectAsync(shareId, hash, new ObjectUpload(stream, ObjectEncoding.Gzip, size, () => Result.Success()), Ct);
    }

    private static async Task<byte[]> ReadAll(IShareStore store, ShareId shareId, ContentHash hash)
    {
        await using StoredObject stored = Unwrap(await store.OpenObjectReadAsync(shareId, hash, Ct));
        return await ReadToEnd(stored.Content);
    }

    private static async Task<byte[]> ReadToEnd(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    /// <summary>Text that compresses well but not trivially: numbered log-like lines.</summary>
    protected static byte[] TextOf(int length)
    {
        StringBuilder text = new(length + 64);
        int line = 0;
        while (text.Length < length)
        {
            line++;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"2026-11-22T10:{line % 60:D2}:{line * 7 % 60:D2}Z INFO request {line * 2654435761L % 1_000_003} served in {line % 97} ms\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString(0, length));
    }

    protected static byte[] Gzip(byte[] content)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.GZipStream gzip = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using System.IO.Compression.GZipStream gzip = new(new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>A read-only, non-seekable stream that remembers whether its last byte was read.</summary>
    private sealed class EndTrackingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public bool ReachedEnd { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Track(await _inner.ReadAsync(buffer, cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Track(int read)
        {
            ReachedEnd |= read == 0;
            return read;
        }
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
