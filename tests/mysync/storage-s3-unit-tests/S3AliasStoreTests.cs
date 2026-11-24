using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Domain.Shares;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>
/// Names on S3: the body is the source of truth (it decides the ETag), metadata a copy for
/// listing; one listing plus a bounded HEAD per alias; conditional deletes.
/// </summary>
public class S3AliasStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ShareId Share = ShareId.TryParse("0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", out ShareId? id) ? id : throw new InvalidOperationException();

    private static readonly string[] ListedKeys = ["aliases/alpha", "aliases/Not_A_Name", "aliases/beta", "aliases/broken", "aliases/gone"];

    private readonly IAmazonS3 _api = Substitute.For<IAmazonS3>();

    private S3AliasStore Store(int parallelism = 4, int? pageSize = null) =>
        new(S3Fakes.Connection(_api), TimeProvider.System, new SyncOptions { MaxParallelism = parallelism }, pageSize, logger: null);

    private static ShareName Name(string value) => ShareName.Create(value).Value;

    [Fact]
    public async Task Create_Should_WriteTheBodyAndTheSameValuesAsMetadata_InOnePut_IfAbsent()
    {
        AliasRecord alias = new(Name("next-wave"), Share, new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), "Ünï@HOST");
        byte[]? body = null;
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            body = S3Fakes.ReadAll(call.Arg<PutObjectRequest>()!.InputStream).Result;
            return new PutObjectResponse { ETag = "\"a1\"" };
        });

        (await Store().CreateAsync(alias, Ct)).Value.ShouldBe("\"a1\"");

        Encoding.UTF8.GetString(body!).ShouldBe("{\"shareId\":\"0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10\",\"createdUtc\":\"2026-11-24T09:00:00+00:00\",\"createdBy\":\"\\u00DCn\\u00EF@HOST\"}");
        await _api.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(request =>
                request!.Key == "aliases/next-wave"
                && request.IfNoneMatch == "*"
                && request.Metadata["x-amz-meta-mysyncshareid"] == Share.ToString()
                && request.Metadata["x-amz-meta-mysynccreatedby"] == "%C3%9Cn%C3%AF%40HOST"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATakenName_Should_BeAlreadyExists()
    {
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.PreconditionFailed());

        (await Store().CreateAsync(new AliasRecord(Name("taken"), Share, DateTimeOffset.UnixEpoch, "x"), Ct)).Error.Code.ShouldBe("Storage.AlreadyExists");
    }

    [Fact]
    public async Task Get_Should_ReadTheBody_NotTheMetadata()
    {
        GetObjectResponse response = new() { ETag = "\"b\"", ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"shareId\":\"{Share}\",\"createdUtc\":\"2026-11-24T09:00:00+00:00\",\"createdBy\":\"me\"}}")) };
        response.Metadata.Add("x-amz-meta-mysyncshareid", ShareId.New().ToString()); // a copy that disagrees: the body wins
        _api.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>()).Returns(response);

        AliasSnapshot read = (await Store().GetAsync(Name("next-wave"), Ct)).Value!;

        read.Alias.ShareId.ShouldBe(Share);
        read.ETag.ShouldBe("\"b\"");
    }

    [Fact]
    public async Task List_Should_BeOneListing_PlusABoundedHeadPerAlias_ReportingDamagedAndStrayOnes()
    {
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(new ListObjectsV2Response
        {
            IsTruncated = false,
            S3Objects = [.. ListedKeys.Select(key => new S3Object { Key = key })],
        });
        int running = 0;
        int most = 0;
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(20, Ct);
            Interlocked.Decrement(ref running);
            string key = call.Arg<GetObjectMetadataRequest>()!.Key;
            if (key == "aliases/gone")
            {
                throw S3Fakes.HeadNotFound(); // deleted since the listing
            }

            GetObjectMetadataResponse head = new();
            if (key != "aliases/broken")
            {
                head.Metadata.Add("x-amz-meta-mysyncshareid", Share.ToString());
                head.Metadata.Add("x-amz-meta-mysynccreatedutc", "2026-11-24T09:00:00Z");
                head.Metadata.Add("x-amz-meta-mysynccreatedby", "me%40HOST");
            }

            return head;
        });

        List<Result<AliasRecord>> listed = [];
        await foreach (Result<AliasRecord> item in Store(parallelism: 2).ListAsync(Ct))
        {
            listed.Add(item);
        }

        listed.Select(item => item.IsSuccess ? item.Value.Name.Value : item.Error.Code).ShouldBe(["alpha", "Storage.StrayAlias", "beta", "Storage.InvalidAlias"]);
        listed[0].Value.CreatedBy.ShouldBe("me@HOST");
        most.ShouldBeLessThanOrEqualTo(2);
        await _api.ReceivedWithAnyArgs(4).GetObjectMetadataAsync(default!, Ct); // the stray key is never read
    }

    [Fact]
    public async Task FindByShare_Should_FilterTheListing_AndSkipDamagedAliases()
    {
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(new ListObjectsV2Response
        {
            IsTruncated = false,
            S3Objects = [new S3Object { Key = "aliases/mine" }, new S3Object { Key = "aliases/broken" }, new S3Object { Key = "aliases/theirs" }],
        });
        _api.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            GetObjectMetadataResponse head = new();
            string key = call.Arg<GetObjectMetadataRequest>()!.Key;
            if (key != "aliases/broken")
            {
                head.Metadata.Add("x-amz-meta-mysyncshareid", key == "aliases/mine" ? Share.ToString() : ShareId.New().ToString());
            }

            return head;
        });

        (await Store().FindByShareAsync(Share, Ct)).Value.Select(alias => alias.Name.Value).ShouldBe(["mine"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey")]
    public async Task AConditionalDelete_Should_BeIfMatch_AndAChangedOrMissingAliasBePreconditionFailed(HttpStatusCode status, string code)
    {
        _api.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(status, code));

        (await Store().DeleteAsync(Name("next-wave"), "\"e1\"", Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
        await _api.Received(1).DeleteObjectAsync(Arg.Is<DeleteObjectRequest>(request => request!.IfMatch == "\"e1\"" && request.Key == "aliases/next-wave"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AConditionalDelete_Denied_Should_SayItNeedsGetObjectToo()
    {
        _api.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(HttpStatusCode.Forbidden, "AccessDenied"));

        Error error = (await Store().DeleteAsync(Name("next-wave"), "\"e1\"", Ct)).Error;

        error.Code.ShouldBe("Storage.Unauthorized");
        error.Description.ShouldContain("s3:GetObject as well as s3:DeleteObject");
    }

    [Fact]
    public void TheBody_Should_BeTheSourceOfTruth_ForAMalformedOne()
    {
        S3AliasStore.FromBody(Name("xyz"), "not json"u8.ToArray()).Error.Code.ShouldBe("Storage.InvalidAlias");
        S3AliasStore.FromBody(Name("xyz"), "{\"shareId\":\"nope\"}"u8.ToArray()).Error.Code.ShouldBe("Storage.InvalidAlias");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (current < value)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
