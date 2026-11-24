using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.State;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>
/// Publishing to a public S3 bucket (CLAUDE.md §10a): the preflight's order and its anonymous checks
/// (against a stub of the public web), native copies (single and multipart), and removals.
/// </summary>
public sealed class S3PublishTargetTests : IDisposable
{
    private const string BaseUrl = "https://team-shares.s3.eu-west-1.amazonaws.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAmazonS3 _api = Substitute.For<IAmazonS3>();
    private readonly AnonymousWeb _web = new();
    private readonly List<string> _operations = [];
    private string? _probeToken;

    public S3PublishTargetTests()
    {
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _operations.Add("location"); return new ListObjectsV2Response { IsTruncated = false }; });
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            PutObjectRequest request = call.Arg<PutObjectRequest>()!;
            _operations.Add("put:" + request.Key);
            if (request.Key.StartsWith(S3PublishTarget.ProbePrefix, StringComparison.Ordinal))
            {
                _probeToken = Encoding.UTF8.GetString(S3Fakes.ReadAll(request.InputStream).Result);
                _web.Serve("/" + request.Key, HttpStatusCode.OK, _probeToken);
            }

            return new PutObjectResponse { ETag = "\"e\"" };
        });
        _api.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => { _operations.Add("delete:" + call.Arg<DeleteObjectRequest>()!.Key); return new DeleteObjectResponse(); });
        _web.Serve("/?list-type=2&prefix=p/", HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code></Error>");
    }

    private S3PublishTarget Target(string? baseUrl = BaseUrl, S3TransferSettings? settings = null) =>
        new(S3Fakes.Connection(_api, settings), Options.Create(new S3PublishOptions { BaseUrl = baseUrl }), _web.Client(), TimeProvider.System, logger: null);

    [Fact]
    public void TheLocation_Should_LinkToIndexHtml_AndCall403And404NotServed()
    {
        PublishLocation location = Target().GetLocation().Value;

        location.Provider.ShouldBe("s3");
        location.AccountName.ShouldBe("s3://team-shares");
        location.ServesIndexDocuments.ShouldBeFalse();
        location.NotServedStatusCodes.ShouldBe(new HashSet<int> { 403, 404 });
    }

    [Theory]
    [InlineData(null, "Configuration.Missing")]
    [InlineData("http://team-shares.s3-website.eu-west-1.amazonaws.com", "Configuration.Invalid")]
    [InlineData("https://x.example.com/?q=1", "Configuration.Invalid")]
    public void ABaseUrlThatIsntHttps_Should_BeRefused(string? baseUrl, string code)
    {
        Error error = Target(baseUrl).GetLocation().Error;

        error.Code.ShouldBe(code);
        if (code == "Configuration.Invalid")
        {
            error.Description.ShouldContain("website endpoint is http only");
        }
    }

    [Fact]
    public void ABucketNameWithDots_Should_BeRefused_SayingWhy()
    {
        Error error = S3Bucket.From("team.publish", "eu-west-1", string.Empty, S3PublishOptions.BucketVariable, S3PublishOptions.RegionVariable).Error;

        error.Description.ShouldContain("MYSYNC_PUBLISH_S3_BUCKET");
        error.Description.ShouldContain("they break HTTPS");
    }

    [Fact]
    public async Task Preflight_Should_CheckTheRegion_ThenServeAProbeAnonymously_ThenRefuseNothingElse()
    {
        (await Target().PreflightAsync(Ct)).IsSuccess.ShouldBeTrue();

        _operations[0].ShouldBe("location");
        _operations[1].ShouldStartWith("put:p/.mysync-probe-");
        _operations[2].ShouldBe("delete:" + _operations[1]["put:".Length..], "the probe is removed");
        _web.Requests.ShouldBe([_operations[1]["put:".Length..].Insert(0, "/"), "/?list-type=2&prefix=p/"]);
        _web.SawCredentials.ShouldBeFalse();
    }

    [Fact]
    public async Task Preflight_Should_StopAtTheWrongRegion_BeforeWritingAnything()
    {
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(HttpStatusCode.MovedPermanently, "PermanentRedirect"));

        (await Target().PreflightAsync(Ct)).Error.Code.ShouldBe("Configuration.WrongRegion");

        _operations.ShouldNotContain(operation => operation.StartsWith("put:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)] // Block Public Access, at the bucket or the account, or an SCP
    [InlineData(HttpStatusCode.NotFound)] // a CloudFront that serves another bucket
    public async Task AProbeNotServedAnonymously_Should_BeNotPublic_AndStillBeRemoved(HttpStatusCode status)
    {
        _web.Override = (path, _) => path.StartsWith("/p/.mysync-probe-", StringComparison.Ordinal) ? (status, "denied") : null;

        Error error = (await Target().PreflightAsync(Ct)).Error;

        error.Code.ShouldBe("Publish.NotPublic");
        error.Description.ShouldContain("arn:aws:s3:::team-shares/p/*");
        _operations.Count(operation => operation.StartsWith("delete:p/.mysync-probe-", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ABucketAnyoneCanList_Should_BeRefused()
    {
        _web.Serve("/?list-type=2&prefix=p/", HttpStatusCode.OK, "<?xml version=\"1.0\"?><ListBucketResult><Contents><Key>p/abc/index.html</Key></Contents></ListBucketResult>");

        (await Target().PreflightAsync(Ct)).Error.Code.ShouldBe("Publish.PublicListing");
    }

    [Fact]
    public async Task CanCopy_Should_AcceptOnlyAnS3ObjectSignedWithTheSameCredentials_AfterPreflight()
    {
        S3PublishTarget target = Target();
        CopySource same = new(new Uri("https://presigned"), new NativeObjectReference("s3", S3Fakes.Region, "team-shares-private", "shares/x/objects/ab/abcd", S3Fakes.Scope));

        target.CanCopy(same).ShouldBeFalse("the credentials aren't known before the preflight");
        (await target.PreflightAsync(Ct)).IsSuccess.ShouldBeTrue();

        target.CanCopy(same).ShouldBeTrue();
        target.CanCopy(same with { Native = same.Native! with { CredentialScope = "aws:another" } }).ShouldBeFalse();
        target.CanCopy(same with { Native = same.Native! with { Provider = "azure-blob" } }).ShouldBeFalse();
        target.CanCopy(new CopySource(new Uri("https://account.blob.core.windows.net/x?sig=abc"))).ShouldBeFalse("S3 can't copy from a URL");
    }

    [Fact]
    public async Task ACopyUpTo5GiB_Should_BeOneCopyObject_ReplacingTheHeaders()
    {
        S3PublishTarget target = Target();
        await target.PreflightAsync(Ct);
        PublishedBlob headers = new("text/plain", "inline", PublishedBlob.Immutable, "gzip");

        (await target.CopyAsync(Native(), 1024, "p/abc/files/a.log", headers, Ct)).Value.ShouldBe(CopyMethod.CopyObject);

        await _api.Received(1).CopyObjectAsync(
            Arg.Is<CopyObjectRequest>(request =>
                request!.SourceBucket == "team-shares-private" && request.SourceKey == "shares/x/objects/ab/abcd"
                && request.DestinationBucket == "team-shares" && request.DestinationKey == "p/abc/files/a.log"
                && request.MetadataDirective == S3MetadataDirective.REPLACE
                && request.ContentType == "text/plain"
                && request.Headers.ContentDisposition == "inline"
                && request.Headers.CacheControl == PublishedBlob.Immutable
                && request.Headers.ContentEncoding == "gzip"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALargerCopy_Should_BeAMultipartCopy_WithTheHeadersOnItsStart()
    {
        S3PublishTarget target = Target(settings: S3Fakes.Small() with { CopyObjectMaxBytes = 100, CopyPartSize = 40 });
        await target.PreflightAsync(Ct);
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        List<(long First, long Last)> ranges = [];
        _api.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            CopyPartRequest request = call.Arg<CopyPartRequest>()!;
            ranges.Add((request.FirstByte!.Value, request.LastByte!.Value));
            return new CopyPartResponse { ETag = $"\"{request.PartNumber}\"" };
        });

        (await target.CopyAsync(Native(), 101, "p/abc/files/big.bin", new PublishedBlob("application/octet-stream", "attachment"), Ct)).Value.ShouldBe(CopyMethod.MultipartCopy);

        ranges.ShouldBe([(0L, 39L), (40L, 79L), (80L, 100L)]);
        await _api.Received(1).InitiateMultipartUploadAsync(
            Arg.Is<InitiateMultipartUploadRequest>(request => request!.ContentType == "application/octet-stream" && request.Headers.ContentDisposition == "attachment"),
            Arg.Any<CancellationToken>());
        await _api.Received(1).CompleteMultipartUploadAsync(Arg.Is<CompleteMultipartUploadRequest>(request => request!.PartETags.Count == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedMultipartCopy_Should_BeAborted()
    {
        S3PublishTarget target = Target(settings: S3Fakes.Small() with { CopyObjectMaxBytes = 10, CopyPartSize = 10 });
        await target.PreflightAsync(Ct);
        _api.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        _api.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.Error(HttpStatusCode.ServiceUnavailable, "SlowDown"));

        (await target.CopyAsync(Native(), 50, "p/abc/files/big.bin", new PublishedBlob("text/plain"), Ct)).Error.Code.ShouldBe("Storage.Unreachable");

        await _api.Received(1).AbortMultipartUploadAsync(Arg.Is<AbortMultipartUploadRequest>(request => request!.UploadId == "u"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletingAPublication_Should_RemoveIndexHtmlFirst_ThenTheRestInBatchesOf1000()
    {
        List<string> keys = ["p/abc/index.html", .. Enumerable.Range(0, 2500).Select(i => $"p/abc/files/{i:D4}.txt")];
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(new ListObjectsV2Response { IsTruncated = false, S3Objects = [.. keys.Select(key => new S3Object { Key = key })] });
        List<int> batches = [];
        _api.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _operations.Add("delete-batch");
            batches.Add(call.Arg<DeleteObjectsRequest>()!.Objects.Count);
            return new DeleteObjectsResponse();
        });

        (await Target().DeletePrefixAsync("p/abc/", Ct)).Value.ShouldBe(2501);

        _operations[0].ShouldBe("delete:p/abc/index.html");
        batches.ShouldBe([1000, 1000, 500]);
    }

    [Fact]
    public async Task AKeyABatchDeleteCouldntRemove_Should_FailTheRemoval()
    {
        _api.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>()).Returns(new ListObjectsV2Response { IsTruncated = false, S3Objects = [new S3Object { Key = "p/abc/a" }, new S3Object { Key = "p/abc/b" }] });
        _api.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteObjectsResponse { DeleteErrors = [new DeleteError { Key = "p/abc/b", Code = "AccessDenied", Message = "denied" }] }); // inside a 200

        Error error = (await Target().DeletePrefixAsync("p/abc/", Ct)).Error;

        error.Code.ShouldBe("Publish.Unauthorized");
    }

    [Fact]
    public async Task APutOnlyIfAbsent_OfAnExistingFile_Should_LeaveItAlone()
    {
        _api.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(S3Fakes.PreconditionFailed());

        (await Target().PutAsync("robots.txt", new MemoryStream("User-agent: *"u8.ToArray()), new PublishedBlob("text/plain; charset=utf-8"), onlyIfAbsent: true, Ct)).Value.ShouldBeFalse();
        await _api.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(request => request!.IfNoneMatch == "*" && request.ContentType == "text/plain; charset=utf-8"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheSameAccountCheck_Should_CompareProviderAndBucket()
    {
        S3PublishTarget target = Target();

        target.SharesAccountWith(new StorageLocation("s3", "eu-west-1", "team-shares")).ShouldBeTrue();
        target.SharesAccountWith(new StorageLocation("s3", "eu-west-1", "team-private")).ShouldBeFalse();
        target.SharesAccountWith(new StorageLocation("azure-blob", "team-shares", "mysync")).ShouldBeFalse();
    }

    public void Dispose() => _web.Dispose();

    private static CopySource Native() =>
        new(null, new NativeObjectReference("s3", S3Fakes.Region, "team-shares-private", "shares/x/objects/ab/abcd", S3Fakes.Scope));

    /// <summary>The public web, as an anonymous client sees the publishing bucket's base URL.</summary>
    private sealed class AnonymousWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _served = [];

        public List<string> Requests { get; } = [];

        public bool SawCredentials { get; private set; }

        public Func<string, string, (HttpStatusCode, string)?>? Override { get; set; }

        public void Serve(string pathAndQuery, HttpStatusCode status, string body) => _served[pathAndQuery] = (status, body);

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            SawCredentials |= request.Headers.Authorization is not null || request.Headers.Contains("x-amz-security-token");
            (HttpStatusCode status, string body) = Override?.Invoke(path, request.RequestUri.Host)
                ?? (_served.TryGetValue(path, out (HttpStatusCode, string) served) ? served : (HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code></Error>"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
