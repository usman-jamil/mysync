using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using MySync.Domain.Configuration;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>What a failed request was doing: the same S3 answer means different things (and needs different hints) per operation.</summary>
internal enum S3Operation
{
    /// <summary>GetObject, HeadObject: a 403 may be a missing key read without <c>s3:ListBucket</c>.</summary>
    Read,

    /// <summary>PutObject, multipart, CopyObject.</summary>
    Write,

    /// <summary>DeleteObject with <c>If-Match</c>, which also needs <c>s3:GetObject</c>.</summary>
    ConditionalDelete,

    /// <summary>DeleteObject(s) without a condition.</summary>
    Delete,

    /// <summary>ListObjectsV2, ListObjectVersions, bucket-level reads.</summary>
    List,
}

/// <summary>The bucket a request was for, so a configuration error can name it.</summary>
/// <param name="BucketVariable">The variable that named the bucket (<c>MYSYNC_S3_BUCKET</c>, <c>MYSYNC_PUBLISH_S3_BUCKET</c>).</param>
internal sealed record S3Target(string Bucket, string Region, string BucketVariable);

/// <summary>
/// AWS SDK failures → <see cref="StorageErrors"/> and <see cref="ConfigurationErrors"/> (CLAUDE.md
/// §11, "S3 error mapping"), the one place. Nothing above this project sees an AWS exception, and no
/// error built here carries SDK text: SDK messages can echo request details, so they only reach the
/// debug log, redacted. The 409 ConditionalRequestConflict and 412 answers are decided by the
/// callers (retry, or AlreadyExists for a create) before anything reaches this mapper.
/// </summary>
internal static partial class S3ErrorMapper
{
    public const string CredentialSources =
        "AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY (with AWS_SESSION_TOKEN), AWS_PROFILE and the shared config and credentials files " +
        "(including SSO and assumed-role profiles), web identity (AWS_WEB_IDENTITY_TOKEN_FILE), the ECS task role, and the EC2 instance role";

    public const string ListBucketHint =
        "Check the AWS credentials' permissions. If this identity lacks s3:ListBucket on the bucket, S3 reports missing objects as access denied.";

    public const string ConditionalDeleteHint =
        "Check the AWS credentials' permissions: a conditional delete needs s3:GetObject as well as s3:DeleteObject.";

    public const string PermissionsHint = "Check the AWS credentials' permissions on the bucket (the IAM policy in the setup guide).";

    public const string CredentialsHint = "Check the AWS credentials: the access key is unknown or the secret doesn't match it.";

    public const string ExpiredHint =
        "The AWS session has expired. For an SSO profile, run `aws sso login` (mysync never runs it, and never asks for credentials); otherwise renew the session credentials.";

    /// <summary>
    /// Whether this is a failure the store reports as a <see cref="Result"/> rather than letting it
    /// propagate. The caller's own cancellation always propagates.
    /// </summary>
    public static bool IsStorageFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested, // a request timeout, not the user
        AmazonServiceException or AmazonClientException or HttpRequestException or SocketException or TimeoutException or WebException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(inner => IsStorageFailure(inner, cancellationToken)),
        _ => false,
    };

    /// <summary>412: the condition (<c>If-Match</c>, <c>If-None-Match: *</c>) is false.</summary>
    public static bool IsPreconditionFailed(Exception exception) =>
        Service(exception) is { StatusCode: HttpStatusCode.PreconditionFailed };

    /// <summary>409 ConditionalRequestConflict: a concurrent request on the key got in the way; the outcome is undecided.</summary>
    public static bool IsConditionalConflict(Exception exception) =>
        Service(exception) is { StatusCode: HttpStatusCode.Conflict } service
        && (service.ErrorCode is "ConditionalRequestConflict" or null or "" || service.ErrorCode.Contains("Conflict", StringComparison.Ordinal));

    /// <summary>The key doesn't exist (404 NoSuchKey, or HEAD's body-less 404).</summary>
    public static bool IsMissingKey(Exception exception) =>
        Service(exception) is { StatusCode: HttpStatusCode.NotFound } service && service.ErrorCode is not "NoSuchBucket" and not "NoSuchUpload";

    public static bool IsMissingBucket(Exception exception) =>
        Service(exception) is { ErrorCode: "NoSuchBucket" };

    public static Error Map(Exception exception, string key, S3Operation operation, S3Target target)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(target);

        if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            return Map(aggregate.InnerExceptions[^1], key, operation, target);
        }

        if (Service(exception) is { } service)
        {
            return FromService(service, key, operation, target);
        }

        return exception switch
        {
            _ when IsMissingCredentials(exception) => ConfigurationErrors.NoCredentials("AWS S3", CredentialSources),
            _ when IsExpiredSession(exception) => StorageErrors.UnauthorizedBecause(ExpiredHint),
            OperationCanceledException or TimeoutException => StorageErrors.Timeout,
            _ => StorageErrors.Unreachable, // no response: DNS, connection, TLS, after the SDK's retries
        };
    }

    /// <summary>The HTTP status, 0 when no response arrived.</summary>
    public static int StatusOf(Exception exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => StatusOf(aggregate.InnerExceptions[^1]),
        _ => Service(exception) is { } service ? (int)service.StatusCode : 0,
    };

    /// <summary>S3's error code (<c>NoSuchKey</c>, <c>AccessDenied</c>, …), or the exception's type when there's none.</summary>
    public static string CodeOf(Exception exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => CodeOf(aggregate.InnerExceptions[^1]),
        _ => Service(exception)?.ErrorCode is { Length: > 0 } code ? code : exception.GetType().Name,
    };

    /// <summary>The credential chain found nothing at all.</summary>
    public static bool IsMissingCredentials(Exception exception) =>
        exception is AmazonClientException && exception.Message.Contains("Failed to resolve AWS credentials", StringComparison.OrdinalIgnoreCase)
        || exception is Amazon.Runtime.CredentialManagement.ProfileNotFoundException;

    /// <summary>An expired SSO token or session: the SSO SDK's own exceptions, or the services' <c>ExpiredToken</c>.</summary>
    public static bool IsExpiredSession(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string? ns = current.GetType().Namespace;
            if (ns is not null && (ns.StartsWith("Amazon.SSO", StringComparison.Ordinal) || ns.StartsWith("Amazon.SSOOIDC", StringComparison.Ordinal)))
            {
                return true;
            }

            if (current is AmazonServiceException { ErrorCode: "ExpiredToken" or "ExpiredTokenException" or "TokenRefreshRequired" or "RequestExpired" })
            {
                return true;
            }

            if (current is AmazonClientException
                && current.Message.Contains("SSO", StringComparison.Ordinal)
                && (current.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("sso login", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("No valid SSO Token", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static Error FromService(AmazonServiceException service, string key, S3Operation operation, S3Target target)
    {
        string body = (service as AmazonS3Exception)?.ResponseBody ?? string.Empty;

        return service.ErrorCode switch
        {
            "NoSuchBucket" => ConfigurationErrors.BucketNotFound(target.BucketVariable, target.Bucket, target.Region),
            "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" or "IncorrectEndpoint"
                => ConfigurationErrors.WrongRegion(target.Bucket, target.Region, RegionFromBody(body, target.Bucket)),
            "RequestTimeTooSkewed" => StorageErrors.ClockSkew(SkewMinutes(body)),
            "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "InvalidClientTokenId" => StorageErrors.UnauthorizedBecause(CredentialsHint),
            "ExpiredToken" or "ExpiredTokenException" or "TokenRefreshRequired" or "RequestExpired" => StorageErrors.UnauthorizedBecause(ExpiredHint),
            "AccessDenied" or "AllAccessDisabled" => StorageErrors.UnauthorizedBecause(AccessDeniedHint(operation)),
            "NoSuchKey" => StorageErrors.NotFound(key),
            "SlowDown" or "ServiceUnavailable" or "InternalError" => StorageErrors.Unreachable,
            "RequestTimeout" => StorageErrors.Timeout,
            _ => FromStatus(service.StatusCode, key, operation, target),
        };
    }

    private static Error FromStatus(HttpStatusCode status, string key, S3Operation operation, S3Target target) => status switch
    {
        HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect => ConfigurationErrors.WrongRegion(target.Bucket, target.Region, actual: null),
        HttpStatusCode.Unauthorized => StorageErrors.UnauthorizedBecause(CredentialsHint),
        HttpStatusCode.Forbidden => StorageErrors.UnauthorizedBecause(AccessDeniedHint(operation)),
        HttpStatusCode.NotFound => StorageErrors.NotFound(key), // HEAD's 404 has no body, so no code
        HttpStatusCode.PreconditionFailed => StorageErrors.PreconditionFailed(key),
        HttpStatusCode.RequestTimeout => StorageErrors.Timeout,
        _ => StorageErrors.Unreachable, // 409 that wasn't retried, 5xx after the SDK's retries, anything else
    };

    // Never "not found": with the documented policy (s3:ListBucket granted) a missing key is a 404.
    private static string AccessDeniedHint(S3Operation operation) => operation switch
    {
        S3Operation.Read => ListBucketHint,
        S3Operation.ConditionalDelete => ConditionalDeleteHint,
        _ => PermissionsHint,
    };

    /// <summary>Where S3 says the bucket is: <c>&lt;Region&gt;</c> (AuthorizationHeaderMalformed), or the region in <c>&lt;Endpoint&gt;</c> (PermanentRedirect).</summary>
    internal static string? RegionFromBody(string body, string bucket)
    {
        if (RegionElement().Match(body) is { Success: true } region && RegionName().IsMatch(region.Groups["region"].Value))
        {
            return region.Groups["region"].Value;
        }

        if (EndpointElement().Match(body) is { Success: true } endpoint)
        {
            // <bucket>.s3.<region>.amazonaws.com, or <bucket>.s3-<region>.amazonaws.com on older regions.
            string host = endpoint.Groups["endpoint"].Value;
            if (host.StartsWith(bucket + ".", StringComparison.Ordinal))
            {
                host = host[(bucket.Length + 1)..];
            }

            if (EndpointRegion().Match(host) is { Success: true } fromHost)
            {
                return fromHost.Groups["region"].Value;
            }

            if (host is "s3.amazonaws.com")
            {
                return "us-east-1";
            }
        }

        return null;
    }

    /// <summary>How far off this machine's clock is, from RequestTimeTooSkewed's <c>&lt;RequestTime&gt;</c> and <c>&lt;ServerTime&gt;</c>.</summary>
    internal static int SkewMinutes(string body)
    {
        DateTimeOffset? request = TimeElement("RequestTime", body);
        DateTimeOffset? server = TimeElement("ServerTime", body);
        if (request is { } sent && server is { } received)
        {
            return (int)Math.Max(1, Math.Round(Math.Abs((received - sent).TotalMinutes)));
        }

        return 15; // S3 refuses requests more than 15 minutes off
    }

    private static DateTimeOffset? TimeElement(string name, string body)
    {
        Match match = Regex.Match(body, $"<{name}>(?<value>[^<]+)</{name}>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["value"].Value.Trim();
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, Utc, out DateTimeOffset exact))
        {
            return exact;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, Utc, out DateTimeOffset parsed) ? parsed : null;
    }

    private static AmazonServiceException? Service(Exception exception) => exception switch
    {
        AmazonServiceException service => service,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Service(aggregate.InnerExceptions[^1]),
        _ => exception.InnerException as AmazonServiceException,
    };

    [GeneratedRegex("<Region>(?<region>[a-z0-9-]+)</Region>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RegionElement();

    [GeneratedRegex("<Endpoint>(?<endpoint>[^<]+)</Endpoint>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EndpointElement();

    [GeneratedRegex(@"^s3[.-](?<region>[a-z]{2}(-[a-z]+)+-\d+)\.amazonaws\.com(\.cn)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EndpointRegion();

    [GeneratedRegex(@"^[a-z]{2}(-[a-z]+)+-\d+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex RegionName();
}
