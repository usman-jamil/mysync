using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>
/// The S3 share store's own rules, against a substitute S3 (its behaviour on real S3 is the
/// contract suite's): conditional outcomes, verified uploads, the 409 restart, encodings, versioning.
/// </summary>
public class S3ShareStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAmazonS3 _api = Substitute.For<IAmazonS3>();

    private S3ShareStore Store(S3TransferSettings? settings = null) => S3Fakes.ShareStore(_api, settings);

    [Fact]
    public void TheLocation_Should_BeProviderS3_TheRegion_AndTheBucket_WithoutTouchingS3()
    {
        Store().GetLocation().Value.ShouldBe(new Domain.State.StorageLocation("s3", S3Fakes.Region, S3Fakes.Bucket));
        _api.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null, "Configuration.Missing", "MYSYNC_S3_BUCKET")]
    [InlineData("team-shares", null, "Configuration.Missing", "MYSYNC_S3_REGION")]
    [InlineData("team.shares", "eu-west-1", "Configuration.Invalid", "dots")]
    [InlineData("Team_Shares", "eu-west-1", "Configuration.Invalid", "not an S3 bucket name")]
    [InlineData("team-shares", "Europe", "Configuration.Invalid", "not an AWS region name")]
    public void ABadBucketOrRegion_Should_BeAConfigurationError(string? bucket, string? region, string code, string mentions)
    {
        Error error = S3Bucket.From(bucket, region, string.Empty, S3StorageOptions.BucketVariable, "MYSYNC_S3_REGION (or AWS_REGION)").Error;

        error.Code.ShouldBe(code);
        error.Description.ShouldContain(mentions);
    }

    [Fact]
    public async Task CreatingAManifest_Should_BeIfNoneMatch_AndA412BeAlreadyExists()
    {
        Manifest manifest = new ManifestBuilder().WithFile("a.txt", "a").Build();
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.PreconditionFailed());

        Error error = (await Store().CreateManifestAsync(manifest.ShareId, manifest, Ct)).Error;

        error.Code.ShouldBe("Storage.AlreadyExists");
        await _api.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(request => request!.IfNoneMatch == "*" && request.Key == ShareLayout.For(manifest.ShareId).Manifest && request.ContentType == "application/json"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey")] // HEAD is gone: re-read too
    public async Task ReplacingAManifest_Should_BeIfMatch_AndAStaleOrMissingHeadBePreconditionFailed(HttpStatusCode status, string code)
    {
        Manifest manifest = new ManifestBuilder().Build(revision: 2);
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(status, code));

        Error error = (await Store().ReplaceManifestAsync(manifest.ShareId, manifest, "\"abc\"", Ct)).Error;

        error.Code.ShouldBe("Storage.PreconditionFailed");
        await _api.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(request => request!.IfMatch == "\"abc\""), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A409OnACommit_Should_NeverCountAsALostRace()
    {
        Manifest manifest = new ManifestBuilder().Build(revision: 2);
        int calls = 0;
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns<PutObjectResponse>(_ =>
            ++calls == 1 ? throw S3Fakes.ConditionalConflict() : new PutObjectResponse { ETag = "\"new\"" });

        (await Store().ReplaceManifestAsync(manifest.ShareId, manifest, "\"old\"", Ct)).Value.ShouldBe("\"new\"");
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneKey_Should_LetOneWin_AndTheRestBeAlreadyExists_NeverATransientError()
    {
        // S3 answers a crowd of conditional creates with one success, 412s, and some 409s whose retry is a 412.
        int calls = 0;
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns<PutObjectResponse>(_ =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                return new PutObjectResponse { ETag = "\"winner\"" };
            }

            throw call % 3 == 0 ? S3Fakes.ConditionalConflict() : S3Fakes.PreconditionFailed();
        });
        var shareId = ShareId.New();
        S3ShareStore store = Store(S3Fakes.Small(conflictAttempts: 5));

        Result<string>[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.CreateManifestAsync(shareId, new ManifestBuilder(shareId).WithFile($"from-{i}", "x").Build(), Ct)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailure).ShouldAllBe(result => result.Error.Code == "Storage.AlreadyExists");
    }

    [Fact]
    public async Task AnObject_Should_BeUploadedOnlyIfAbsent_WithItsMetadata()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello");
        ContentHash hash = ManifestBuilder.HashOf(content);
        var shareId = ShareId.New();
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.HeadNotFound());
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse { ETag = "\"e\"" });

        (await Store().UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        await _api.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(request =>
                request!.Key == ShareLayout.For(shareId).ObjectBlob(hash)
                && request.IfNoneMatch == "*"
                && request.ContentType == "application/octet-stream"
                && request.Metadata["x-amz-meta-sha256"] == hash.Value
                && request.Metadata["x-amz-meta-size"] == "5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnObjectAlreadyStored_Should_NotBeUploadedAgain()
    {
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).Returns(new GetObjectMetadataResponse());

        (await Store().UploadObjectAsync(ShareId.New(), ManifestBuilder.HashOf("x"), ObjectUpload.Raw(new MemoryStream("x"u8.ToArray()), 1), Ct)).IsSuccess.ShouldBeTrue();

        await _api.DidNotReceiveWithAnyArgs().PutObjectAsync(default!, Ct);
    }

    [Fact]
    public async Task BytesThatDontMatchTheirHash_Should_StoreNothing_EvenInAMultipartUpload()
    {
        byte[] content = new byte[5000];
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.HeadNotFound());
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        _api.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadPartResponse { ETag = "\"p\"" });

        Error error = (await Store().UploadObjectAsync(ShareId.New(), ManifestBuilder.HashOf("something else"), ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).Error;

        error.Code.ShouldBe("Storage.HashMismatch");
        await _api.DidNotReceiveWithAnyArgs().CompleteMultipartUploadAsync(default!, Ct);
        await _api.ReceivedWithAnyArgs(1).AbortMultipartUploadAsync(default!, Ct);
    }

    [Fact]
    public async Task A409OnCompletingAnObject_Should_RestartTheUpload_WhenNobodyElseStoredIt()
    {
        byte[] content = [.. Enumerable.Range(0, 3000).Select(i => (byte)i)];
        ContentHash hash = ManifestBuilder.HashOf(content);
        int completions = 0;
        int heads = 0;
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).Returns<GetObjectMetadataResponse>(_ =>
        {
            heads++;
            throw S3Fakes.HeadNotFound();
        });
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        List<byte> uploaded = [];
        _api.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            uploaded.AddRange(S3Fakes.ReadAll(call.Arg<UploadPartRequest>()!.InputStream).Result);
            return new UploadPartResponse { ETag = "\"p\"" };
        });
        _api.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns<CompleteMultipartUploadResponse>(_ =>
            ++completions == 1 ? throw S3Fakes.ConditionalConflict() : new CompleteMultipartUploadResponse { ETag = "\"done\"" });

        (await Store().UploadObjectAsync(ShareId.New(), hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        completions.ShouldBe(2);
        heads.ShouldBe(2, "once before uploading, once after the 409");
        uploaded.Count.ShouldBe(6000, "the whole upload again, from the start of the content");
        await _api.ReceivedWithAnyArgs(1).AbortMultipartUploadAsync(default!, Ct);
    }

    [Fact]
    public async Task A409OnCompletingAnObject_Should_BeASuccess_WhenTheCompetingUploadStoredIt()
    {
        byte[] content = new byte[3000];
        int heads = 0;
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).Returns<GetObjectMetadataResponse>(_ =>
            ++heads == 1 ? throw S3Fakes.HeadNotFound() : new GetObjectMetadataResponse());
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        _api.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadPartResponse { ETag = "\"p\"" });
        _api.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.ConditionalConflict());

        (await Store().UploadObjectAsync(ShareId.New(), ManifestBuilder.HashOf(content), ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();

        await _api.ReceivedWithAnyArgs(1).CompleteMultipartUploadAsync(default!, Ct);
    }

    [Fact]
    public async Task AGzipUpload_Should_CarryContentEncodingGzip_AndTheUncompressedSize()
    {
        byte[] stored = [1, 2, 3, 4];
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.HeadNotFound());
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse { ETag = "\"g\"" });

        await Store().UploadObjectAsync(ShareId.New(), ManifestBuilder.HashOf("x"), new ObjectUpload(new MemoryStream(stored), ObjectEncoding.Gzip, 100, () => Result.Success()), Ct);

        await _api.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(request =>
                request!.Headers.ContentEncoding == "gzip"
                && request.Metadata["x-amz-meta-size"] == "100"
                && request.Metadata["x-amz-meta-storedsize"] == "4"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, ObjectEncoding.Identity)]
    [InlineData("gzip", ObjectEncoding.Gzip)]
    [InlineData("gzip,aws-chunked", ObjectEncoding.Gzip)] // an upload's transfer framing, not the object's
    [InlineData("aws-chunked", ObjectEncoding.Identity)]
    public void TheStoredEncoding_Should_IgnoreAwsChunked(string? contentEncoding, ObjectEncoding expected) =>
        S3ShareStore.EncodingOf(contentEncoding).Value.ShouldBe(expected);

    [Fact]
    public void AnUnknownEncoding_Should_BeUnsupported() =>
        S3ShareStore.EncodingOf("br").Error.Code.ShouldBe("Storage.UnsupportedEncoding");

    [Theory]
    [InlineData(null, "us-east-1")]
    [InlineData("", "us-east-1")]
    [InlineData("EU", "eu-west-1")]
    [InlineData("ap-southeast-2", "ap-southeast-2")]
    public void GetBucketLocation_Should_BeReadAsARegion(string? constraint, string region) =>
        S3ShareStore.RegionOf(constraint).ShouldBe(region);

    [Fact]
    public async Task EnsureReady_Should_RefuseABucketInAnotherRegion_AndNeverCreateOne()
    {
        _api.GetBucketLocationAsync(Arg.Any<GetBucketLocationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetBucketLocationResponse { Location = new S3Region("us-west-2") });

        Error error = (await Store().EnsureReadyForNewShareAsync(Ct)).Error;

        error.Code.ShouldBe("Configuration.WrongRegion");
        error.Description.ShouldContain("is in us-west-2, not eu-west-1");
        await _api.DidNotReceiveWithAnyArgs().PutBucketAsync(default(PutBucketRequest)!, Ct);
    }

    [Fact]
    public async Task RecoverableCopies_Should_BeZero_WithoutVersioning_AndCountOldVersionsAndMarkers_WithIt()
    {
        _api.GetBucketVersioningAsync(Arg.Any<GetBucketVersioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetBucketVersioningResponse { VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Off } });
        (await Store().CountRecoverableCopiesAsync(ShareId.New(), Ct)).Value.ShouldBe(0);
        await _api.DidNotReceiveWithAnyArgs().ListVersionsAsync(default(ListVersionsRequest)!, Ct);

        _api.GetBucketVersioningAsync(Arg.Any<GetBucketVersioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetBucketVersioningResponse { VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        _api.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ListVersionsResponse
            {
                IsTruncated = true,
                NextKeyMarker = "k",
                NextVersionIdMarker = "v",
                Versions = [new S3ObjectVersion { IsLatest = true, IsDeleteMarker = true }, new S3ObjectVersion { IsLatest = false }],
            },
            new ListVersionsResponse { IsTruncated = false, Versions = [new S3ObjectVersion { IsLatest = false }, new S3ObjectVersion { IsLatest = true }] });

        (await Store().CountRecoverableCopiesAsync(ShareId.New(), Ct)).Value.ShouldBe(3);
    }

    [Fact]
    public async Task ListingObjects_Should_ReadEachOnesEncoding_AndSkipStrayKeys()
    {
        var shareId = ShareId.New();
        var layout = ShareLayout.For(shareId);
        ContentHash raw = ManifestBuilder.HashOf("raw");
        ContentHash gzip = ManifestBuilder.HashOf("gzip");
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(new ListObjectsV2Response
        {
            IsTruncated = false,
            S3Objects =
            [
                new S3Object { Key = layout.ObjectBlob(raw), Size = 3, LastModified = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc) },
                new S3Object { Key = layout.ObjectsPrefix + "stray.txt", Size = 1 },
                new S3Object { Key = layout.ObjectBlob(gzip), Size = 9, LastModified = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc) },
            ],
        });
        _api.GetObjectMetadataAsync(Arg.Is<GetObjectMetadataRequest>(request => request!.Key == layout.ObjectBlob(gzip)), Arg.Any<CancellationToken>())
            .Returns(_ => { GetObjectMetadataResponse head = new(); head.Headers.ContentEncoding = "gzip"; return head; });
        _api.GetObjectMetadataAsync(Arg.Is<GetObjectMetadataRequest>(request => request!.Key == layout.ObjectBlob(raw)), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse());

        List<StoredObjectInfo> listed = [];
        await foreach (Result<StoredObjectInfo> item in Store().ListObjectsAsync(shareId, Ct))
        {
            listed.Add(item.Value);
        }

        listed.Select(item => (item.Hash, item.Size, item.Encoding)).ShouldBe([(raw, 3L, ObjectEncoding.Identity), (gzip, 9L, ObjectEncoding.Gzip)]);
    }

    [Fact]
    public async Task ACopySource_Should_NameTheObjectNatively_AndOfferAPresignedUrl_ForAnHourAtMost()
    {
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf("x");
        _api.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://team-shares.s3.eu-west-1.amazonaws.com/key?X-Amz-Signature=abc");

        CopySource source = (await Store().GetCopySourceAsync(shareId, hash, TimeSpan.FromHours(5), Ct)).Value!;

        source.Native.ShouldBe(new NativeObjectReference("s3", S3Fakes.Region, S3Fakes.Bucket, ShareLayout.For(shareId).ObjectBlob(hash), S3Fakes.Scope));
        source.Url.ShouldNotBeNull();
        await _api.Received(1).GetPreSignedURLAsync(Arg.Is<GetPreSignedUrlRequest>(request =>
            request!.Verb == HttpVerb.GET && request.Expires <= DateTime.UtcNow.AddHours(1).AddMinutes(1)));
    }

    [Fact]
    public async Task AnAccessDeniedRead_Should_BeUnauthorized_NeverAMissingManifest()
    {
        _api.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(HttpStatusCode.Forbidden, "AccessDenied"));

        Error error = (await Store().GetManifestAsync(ShareId.New(), Ct)).Error;

        error.Code.ShouldBe("Storage.Unauthorized");
        error.Description.ShouldContain("s3:ListBucket");
    }

    [Fact]
    public async Task NoCredentials_Should_StopEveryCall_WithOneConfigurationError()
    {
        int connects = 0;
        S3Connection connection = new(
            () => S3Fakes.TheBucket(),
            (_, _) =>
            {
                connects++;
                return Task.FromResult(Result.Failure<S3Client>(S3Connection.CredentialError(new Amazon.Runtime.AmazonClientException("Failed to resolve AWS credentials."))));
            },
            S3Fakes.Small());
        S3ShareStore store = new(connection, S3Fakes.Serializer, S3Fakes.RegistrySerializer, TimeProvider.System, options: null, logger: null);

        (await store.GetManifestAsync(ShareId.New(), Ct)).Error.Code.ShouldBe("Configuration.Missing");
        (await store.ObjectExistsAsync(ShareId.New(), ManifestBuilder.HashOf("x"), Ct)).Error.Code.ShouldBe("Configuration.Missing");
        connects.ShouldBe(1, "credentials are resolved once");
        store.GetLocation().IsSuccess.ShouldBeTrue("the location needs no credentials");
    }
}
