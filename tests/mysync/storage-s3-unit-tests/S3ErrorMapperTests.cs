using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using MySync.Domain.Storage;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>Every row of the S3 error mapping (CLAUDE.md §11): codes, hints, and that no SDK text gets into a description.</summary>
public class S3ErrorMapperTests
{
    private static readonly S3Target Target = new(S3Fakes.Bucket, S3Fakes.Region, S3StorageOptions.BucketVariable);

    private static Error Map(Exception exception, S3Operation operation = S3Operation.Read) =>
        S3ErrorMapper.Map(exception, "shares/x/manifest.json", operation, Target);

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", "Storage.NotFound")]
    [InlineData(HttpStatusCode.NotFound, "NotFound", "Storage.NotFound")] // HEAD: no body
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "Storage.PreconditionFailed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", "Storage.Unreachable")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", "Storage.Unreachable")]
    [InlineData(HttpStatusCode.Conflict, "ConditionalRequestConflict", "Storage.Unreachable")] // only if it escaped the retries
    [InlineData(HttpStatusCode.BadRequest, "RequestTimeout", "Storage.Timeout")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", "Configuration.BucketNotFound")]
    [InlineData(HttpStatusCode.MovedPermanently, "PermanentRedirect", "Configuration.WrongRegion")]
    [InlineData(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", "Configuration.WrongRegion")]
    [InlineData(HttpStatusCode.BadRequest, "IllegalLocationConstraintException", "Configuration.WrongRegion")]
    [InlineData(HttpStatusCode.Forbidden, "RequestTimeTooSkewed", "Storage.ClockSkew")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", "Storage.Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId", "Storage.Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "Storage.Unauthorized")]
    [InlineData(HttpStatusCode.BadRequest, "ExpiredToken", "Storage.Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "", "Storage.Unauthorized")]
    public void ServiceErrors_Should_MapToTheirStorageOrConfigurationError(HttpStatusCode status, string code, string expected)
    {
        Error error = Map(S3Fakes.Error(status, code));

        error.Code.ShouldBe(expected);
        error.Description.ShouldNotContain("(test)"); // never the SDK's message
    }

    [Fact]
    public void AccessDenied_OnARead_Should_NeverBeNotFound_ButHintAtListBucket()
    {
        Error error = Map(S3Fakes.Error(HttpStatusCode.Forbidden, "AccessDenied"), S3Operation.Read);

        error.Code.ShouldBe("Storage.Unauthorized");
        error.Description.ShouldContain("s3:ListBucket");
        error.Description.ShouldContain("reports missing objects as access denied");
    }

    [Fact]
    public void AccessDenied_OnAConditionalDelete_Should_HintAtGetObject()
    {
        Error error = Map(S3Fakes.Error(HttpStatusCode.Forbidden, "AccessDenied"), S3Operation.ConditionalDelete);

        error.Description.ShouldContain("s3:GetObject");
        error.Description.ShouldContain("s3:DeleteObject");
    }

    [Theory]
    [InlineData("No valid SSO Token could be found.")] // an SSO profile nobody logged in with (the SDK's own words)
    [InlineData("The SSO session associated with this profile has expired or is otherwise invalid.")]
    public void AnSsoProfileWithoutAToken_Should_SayRunAwsSsoLogin(string message)
    {
        Error error = S3Connection.CredentialError(new AmazonClientException(message));

        error.Code.ShouldBe("Storage.Unauthorized");
        error.Description.ShouldContain("aws sso login");
    }

    [Fact]
    public void AnSsoPortalOrStsOutOfReach_WhileResolvingCredentials_Should_BeUnreachable()
    {
        S3Connection.CredentialError(new HttpRequestException("Resource temporarily unavailable (portal.sso.eu-west-1.amazonaws.com:443)")).ShouldBe(StorageErrors.Unreachable);
        S3Connection.CredentialError(new AmazonClientException("Error calling AssumeRole", new HttpRequestException("no route"))).ShouldBe(StorageErrors.Unreachable);
        S3Connection.CredentialError(new AmazonClientException("something else")).Code.ShouldBe("Storage.Unauthorized");
    }

    [Fact]
    public void AnExpiredSession_Should_SayRunAwsSsoLogin_AndNeverRunIt()
    {
        Error error = Map(S3Fakes.Error(HttpStatusCode.BadRequest, "ExpiredToken"));

        error.Description.ShouldContain("aws sso login");
        error.Description.ShouldContain("never runs it");
    }

    [Fact]
    public void AWrongRegion_Should_NameTheBucket_TheConfiguredRegion_AndTheRightOne()
    {
        Error malformed = Map(S3Fakes.Error(
            HttpStatusCode.BadRequest,
            "AuthorizationHeaderMalformed",
            "<Error><Code>AuthorizationHeaderMalformed</Code><Message>the region 'eu-west-1' is wrong; expecting 'ap-southeast-2'</Message><Region>ap-southeast-2</Region></Error>"));
        Error redirect = Map(S3Fakes.Error(
            HttpStatusCode.MovedPermanently,
            "PermanentRedirect",
            $"<Error><Code>PermanentRedirect</Code><Endpoint>{S3Fakes.Bucket}.s3.us-west-2.amazonaws.com</Endpoint><Bucket>{S3Fakes.Bucket}</Bucket></Error>"));
        Error unknown = Map(S3Fakes.Error(HttpStatusCode.MovedPermanently, "PermanentRedirect"));

        malformed.Description.ShouldBe($"The bucket '{S3Fakes.Bucket}' is in ap-southeast-2, not eu-west-1. Set MYSYNC_S3_REGION=ap-southeast-2 (or AWS_REGION).");
        redirect.Description.ShouldContain("is in us-west-2, not eu-west-1");
        unknown.Description.ShouldContain($"The bucket '{S3Fakes.Bucket}' isn't in eu-west-1");
    }

    [Theory]
    [InlineData("s3.amazonaws.com", "us-east-1")]
    [InlineData("s3-eu-west-1.amazonaws.com", "eu-west-1")]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    public void TheRegion_Should_BeReadFromARedirectsEndpoint(string endpoint, string region) =>
        S3ErrorMapper.RegionFromBody($"<Endpoint>{S3Fakes.Bucket}.{endpoint}</Endpoint>", S3Fakes.Bucket).ShouldBe(region);

    [Fact]
    public void ClockSkew_Should_SayByHowMuch_AndBeItsOwnError_NotUnauthorized()
    {
        Error error = Map(S3Fakes.Error(
            HttpStatusCode.Forbidden,
            "RequestTimeTooSkewed",
            "<Error><Code>RequestTimeTooSkewed</Code><RequestTime>20261124T101500Z</RequestTime><ServerTime>2026-11-24T09:52:00Z</ServerTime><MaxAllowedSkewMilliseconds>900000</MaxAllowedSkewMilliseconds></Error>"));

        error.Code.ShouldBe("Storage.ClockSkew");
        error.Description.ShouldContain("off by about 23 minute(s)");
        error.Description.ShouldContain("Fix the clock");
        S3ErrorMapper.SkewMinutes(string.Empty).ShouldBe(15);
    }

    [Fact]
    public void ABucketThatDoesntExist_Should_NameItAndTheRegion()
    {
        Error error = Map(S3Fakes.Error(HttpStatusCode.NotFound, "NoSuchBucket"));

        error.Description.ShouldBe($"The bucket '{S3Fakes.Bucket}' (MYSYNC_S3_BUCKET) doesn't exist in eu-west-1. Check the bucket name and region.");
    }

    [Fact]
    public void NoResponse_Should_BeUnreachableOrTimeout()
    {
        Map(new HttpRequestException("dns")).ShouldBe(StorageErrors.Unreachable);
        Map(new SocketException()).ShouldBe(StorageErrors.Unreachable);
        Map(new TimeoutException()).ShouldBe(StorageErrors.Timeout);
        Map(new AggregateException(new HttpRequestException("1"), new TimeoutException())).ShouldBe(StorageErrors.Timeout);
    }

    [Fact]
    public void NoCredentials_Should_BeAConfigurationError_ListingTheSources()
    {
        Error error = Map(new AmazonClientException("Failed to resolve AWS credentials. The credential providers used to search for credentials returned the following errors: ..."));

        error.Code.ShouldBe("Configuration.Missing");
        error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
        error.Description.ShouldContain("AWS_ACCESS_KEY_ID");
        error.Description.ShouldContain("AWS_PROFILE");
        error.Description.ShouldContain("EC2 instance role");
        error.Description.ShouldContain("never asks for them");
    }

    [Fact]
    public void TheCallersOwnCancellation_Should_Propagate_ButARequestTimeoutNot()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        S3ErrorMapper.IsStorageFailure(new OperationCanceledException(), cancelled.Token).ShouldBeFalse();
        S3ErrorMapper.IsStorageFailure(new OperationCanceledException(), CancellationToken.None).ShouldBeTrue();
        S3ErrorMapper.IsStorageFailure(new InvalidOperationException(), CancellationToken.None).ShouldBeFalse();
    }

    [Fact]
    public void TheConditionalAnswers_Should_BeRecognised()
    {
        S3ErrorMapper.IsPreconditionFailed(S3Fakes.PreconditionFailed()).ShouldBeTrue();
        S3ErrorMapper.IsConditionalConflict(S3Fakes.ConditionalConflict()).ShouldBeTrue();
        S3ErrorMapper.IsConditionalConflict(S3Fakes.PreconditionFailed()).ShouldBeFalse();
        S3ErrorMapper.IsMissingKey(S3Fakes.NoSuchKey()).ShouldBeTrue();
        S3ErrorMapper.IsMissingKey(S3Fakes.HeadNotFound()).ShouldBeTrue();
        S3ErrorMapper.IsMissingKey(S3Fakes.Error(HttpStatusCode.NotFound, "NoSuchBucket")).ShouldBeFalse();
    }
}
