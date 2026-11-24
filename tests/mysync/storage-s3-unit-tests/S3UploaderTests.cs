using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>
/// Writing objects of any length (CLAUDE.md §11, "S3 differences"): one PutObject when the content
/// ends inside the first part, else a multipart upload; the condition and the 409 rules; aborts.
/// </summary>
public class S3UploaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAmazonS3 _api = Substitute.For<IAmazonS3>();
    private readonly Captured _captured = new();
    private readonly S3Client _client;

    public S3UploaderTests()
    {
        _client = new S3Client(_api, S3Fakes.Scope);
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            PutObjectRequest request = call.Arg<PutObjectRequest>()!;
            _captured.Add("put", request.Key, request.InputStream);
            return new PutObjectResponse { ETag = "\"etag-put\"" };
        });
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _captured.Add("initiate", call.Arg<InitiateMultipartUploadRequest>()!.Key);
            return new InitiateMultipartUploadResponse { UploadId = "upload-1" };
        });
        _api.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            UploadPartRequest request = call.Arg<UploadPartRequest>()!;
            _captured.Add($"part:{request.PartNumber}", request.Key, request.InputStream);
            return new UploadPartResponse { ETag = $"\"part-{request.PartNumber}\"" };
        });
        _api.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _captured.Add("complete", call.Arg<CompleteMultipartUploadRequest>()!.Key);
            return new CompleteMultipartUploadResponse { ETag = "\"etag-multipart-3\"" };
        });
        _api.AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _captured.Add("abort", call.Arg<AbortMultipartUploadRequest>()!.Key);
            return new AbortMultipartUploadResponse();
        });
    }

    private Task<Result<S3Written>> Upload(byte[] bytes, long? knownLength, S3Condition? condition = null, Func<Result>? beforeCommit = null, S3TransferSettings? settings = null, Stream? content = null, CancellationToken? token = null) =>
        new S3Uploader(settings ?? S3Fakes.Small(partSize: 1024), TimeProvider.System, NullLogger.Instance).UploadAsync(
            _client, S3Fakes.TheBucket(), "k", content ?? new MemoryStream(bytes), _ => new S3Headers("application/octet-stream", "gzip"),
            condition ?? S3Condition.IfAbsent, knownLength, sizeHint: bytes.Length, beforeCommit, token ?? Ct);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)] // exactly one part: the look-ahead finds nothing more
    public async Task ContentThatEndsInsideThePart_Should_BeOnePutObject_WithTheCondition(int length)
    {
        byte[] bytes = [.. Enumerable.Range(0, length).Select(i => (byte)i)];

        S3Written written = (await Upload(bytes, knownLength: null)).Value;

        written.Outcome.ShouldBe(S3WriteOutcome.Stored);
        written.ETag.ShouldBe("\"etag-put\"");
        _captured.Operations.ShouldBe(["put"]);
        _captured.Calls.Single().Bytes.ShouldBe(bytes);
        await _api.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(request => request!.IfNoneMatch == "*" && request!.Headers.ContentEncoding == "gzip"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LongerContent_Should_BeAMultipartUpload_PartByPart_CompletedWithTheCondition()
    {
        byte[] bytes = [.. Enumerable.Range(0, 2500).Select(i => (byte)(i % 251))];

        S3Written written = (await Upload(bytes, knownLength: bytes.Length)).Value;

        written.Outcome.ShouldBe(S3WriteOutcome.Stored);
        written.Length.ShouldBe(2500);
        _captured.Operations.ShouldBe(["initiate", "part:1", "part:2", "part:3", "complete"]);
        _captured.Calls.Where(call => call.Operation.StartsWith("part:", StringComparison.Ordinal)).SelectMany(call => call.Bytes).ShouldBe(bytes);
        await _api.Received(1).CompleteMultipartUploadAsync(
            Arg.Is<CompleteMultipartUploadRequest>(request => request!.IfNoneMatch == "*" && request!.PartETags.Count == 3 && request!.UploadId == "upload-1"),
            Arg.Any<CancellationToken>());
        await _api.Received(1).InitiateMultipartUploadAsync(Arg.Is<InitiateMultipartUploadRequest>(request => request!.Headers.ContentEncoding == "gzip"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheVerdict_Should_ComeAfterTheLastByte_AndAFailureStoreNothing()
    {
        byte[] bytes = new byte[3000];
        bool askedAfterAllParts = false;

        Result<S3Written> result = await Upload(bytes, knownLength: null, beforeCommit: () =>
        {
            askedAfterAllParts = _captured.Operations.Count(operation => operation.StartsWith("part:", StringComparison.Ordinal)) == 3;
            return Result.Failure(Domain.Storage.StorageErrors.HashMismatch("k"));
        });

        result.Error.Code.ShouldBe("Storage.HashMismatch");
        askedAfterAllParts.ShouldBeTrue();
        _captured.Operations.ShouldNotContain("complete");
        _captured.Operations[^1].ShouldBe("abort");
    }

    [Fact]
    public async Task AFailedVerdict_OnASinglePart_Should_PutNothing()
    {
        Result<S3Written> result = await Upload([1, 2, 3], knownLength: null, beforeCommit: () => Result.Failure(Domain.Storage.StorageErrors.HashMismatch("k")));

        result.IsFailure.ShouldBeTrue();
        _captured.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedPart_Should_AbortTheUpload_AndFail()
    {
        _api.UploadPartAsync(Arg.Is<UploadPartRequest>(request => request!.PartNumber == 2), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network"));

        await Should.ThrowAsync<HttpRequestException>(() => Upload(new byte[3000], knownLength: 3000));

        _captured.Operations.ShouldBe(["initiate", "part:1", "abort"]);
    }

    [Fact]
    public async Task ACancelledUpload_Should_BeAborted_WithItsPartsNotLeftBehind()
    {
        using CancellationTokenSource cancel = new();
        _api.UploadPartAsync(Arg.Is<UploadPartRequest>(request => request!.PartNumber == 2), Arg.Any<CancellationToken>()).Returns<UploadPartResponse>(_ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Upload(new byte[3000], knownLength: 3000, token: cancel.Token));

        _captured.Operations[^1].ShouldBe("abort");
        await _api.Received(1).AbortMultipartUploadAsync(Arg.Is<AbortMultipartUploadRequest>(request => request!.UploadId == "upload-1"), CancellationToken.None);
    }

    [Fact]
    public async Task A409OnPutObject_Should_BeRetried_UntilItGivesARealAnswer()
    {
        int calls = 0;
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns<PutObjectResponse>(call =>
        {
            _captured.Add("put", "k", call.Arg<PutObjectRequest>()!.InputStream);
            return ++calls < 3 ? throw S3Fakes.ConditionalConflict() : new PutObjectResponse { ETag = "\"third\"" };
        });

        S3Written written = (await Upload([7, 8, 9], knownLength: 3)).Value;

        written.ETag.ShouldBe("\"third\"");
        _captured.Calls.Select(call => call.Bytes).ShouldAllBe(bytes => bytes.SequenceEqual(new byte[] { 7, 8, 9 }), "every try sends the whole body again");
    }

    [Fact]
    public async Task A409_ThatKeepsComing_Should_BeUnreachable_NeverALostRace()
    {
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.ConditionalConflict());

        Result<S3Written> result = await Upload([1], knownLength: 1);

        result.Error.Code.ShouldBe("Storage.Unreachable");
        await _api.Received(3).PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A412_Should_BeConditionFailed_AndNotRetried()
    {
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.PreconditionFailed());

        (await Upload([1], knownLength: 1)).Value.Outcome.ShouldBe(S3WriteOutcome.ConditionFailed);
        await _api.Received(1).PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnIfMatchWrite_ToAKeyThatsGone_Should_BeConditionFailed()
    {
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.NoSuchKey());

        (await Upload([1], knownLength: 1, condition: S3Condition.IfMatch("\"old\""))).Value.Outcome.ShouldBe(S3WriteOutcome.ConditionFailed);
        await _api.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(request => request!.IfMatch == "\"old\"" && request!.IfNoneMatch == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A409OnCompletion_Should_AbortTheUpload_AndSayConflict_NotRetryTheCompletion()
    {
        _api.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.ConditionalConflict());

        S3Written written = (await Upload(new byte[2048 + 1], knownLength: null)).Value;

        written.Outcome.ShouldBe(S3WriteOutcome.Conflict);
        await _api.Received(1).CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>());
        await _api.Received(1).AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A412OnCompletion_Should_BeConditionFailed_AndTheUploadAborted()
    {
        _api.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.PreconditionFailed());

        (await Upload(new byte[5000], knownLength: 5000)).Value.Outcome.ShouldBe(S3WriteOutcome.ConditionFailed);
        _captured.Operations[^1].ShouldBe("abort");
    }

    [Fact]
    public async Task AnUnknownLength_Should_GrowItsParts_AndFailClearly_BeforePart10001()
    {
        // 1-byte parts doubling every 1,000 parts hold 1,023,000 bytes in 10,000 parts; one byte more is too large.
        S3TransferSettings tiny = S3Fakes.Small(partSize: 1);
        byte[] bytes = new byte[1_023_001];

        Result<S3Written> result = await new S3Uploader(tiny, TimeProvider.System, NullLogger.Instance).UploadAsync(
            _client, S3Fakes.TheBucket(), "k", new MemoryStream(bytes), _ => new S3Headers("application/octet-stream"), S3Condition.IfAbsent,
            knownLength: null, sizeHint: 0, beforeCommit: null, Ct);

        result.Error.Code.ShouldBe("Storage.TooLarge");
        _captured.Operations.Count(operation => operation.StartsWith("part:", StringComparison.Ordinal)).ShouldBe(S3Limits.MaxParts);
        _captured.Operations.ShouldNotContain("complete");
        _captured.Operations[^1].ShouldBe("abort");
    }

    [Fact]
    public async Task AKnownLengthAboveFiveTiB_Should_BeTooLarge_BeforeAnythingIsSent()
    {
        Result<S3Written> result = await Upload([], knownLength: S3Limits.MaxObjectSize + 1);

        result.Error.Code.ShouldBe("Storage.TooLarge");
        _captured.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoAcl_Should_EverBeSent()
    {
        await Upload([1, 2], knownLength: 2);

        await _api.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(request => request!.CannedACL == null && (request!.Grants == null || request!.Grants.Count == 0)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheBackoff_Should_DoubleWithJitter_UpToFiveSeconds()
    {
        S3Uploader uploader = new(S3Fakes.Small() with { ConflictBaseDelay = TimeSpan.FromMilliseconds(100) }, TimeProvider.System, NullLogger.Instance);

        uploader.Backoff(1).TotalMilliseconds.ShouldBeInRange(50, 100);
        uploader.Backoff(3).TotalMilliseconds.ShouldBeInRange(200, 400);
        uploader.Backoff(20).TotalMilliseconds.ShouldBeInRange(2500, 5000);
    }
}
