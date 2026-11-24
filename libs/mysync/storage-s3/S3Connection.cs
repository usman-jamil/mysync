using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Domain.Configuration;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>
/// A configured bucket: its name, region, and (tests only) a key prefix. Validated from the
/// options without any network or credentials, so <c>GetLocation</c> stays offline.
/// </summary>
internal sealed partial record S3Bucket(string Name, string Region, string KeyPrefix, S3Target Target)
{
    /// <summary>The object key for a <c>ShareLayout</c> / <c>PublicationLayout</c> name.</summary>
    public string Key(string layoutName) => KeyPrefix + layoutName;

    /// <summary>The layout name of a listed key; <c>null</c> for one outside the prefix.</summary>
    public string? LayoutName(string key) => key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key[KeyPrefix.Length..] : null;

    /// <param name="regionVariable">Named in the error when no region is set.</param>
    public static Result<S3Bucket> From(string? bucket, string? region, string keyPrefix, string bucketVariable, string regionVariable)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            return Result.Failure<S3Bucket>(ConfigurationErrors.Missing(bucketVariable));
        }

        string name = bucket.Trim();
        if (!BucketName().IsMatch(name))
        {
            return Result.Failure<S3Bucket>(ConfigurationErrors.Invalid(
                bucketVariable,
                name.Contains('.', StringComparison.Ordinal)
                    ? $"'{name}' contains dots. mysync needs a bucket name without dots: they break HTTPS for the bucket's address (<bucket>.s3.<region>.amazonaws.com)."
                    : $"'{name}' is not an S3 bucket name (3-63 lowercase letters, digits and hyphens, starting and ending with a letter or digit)."));
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            return Result.Failure<S3Bucket>(ConfigurationErrors.Missing(regionVariable));
        }

        string regionName = region.Trim();
        if (!S3ErrorMapper.RegionName().IsMatch(regionName))
        {
            return Result.Failure<S3Bucket>(ConfigurationErrors.Invalid(regionVariable, $"'{regionName}' is not an AWS region name (e.g. us-east-1, eu-west-1)."));
        }

        return new S3Bucket(name, regionName, keyPrefix, new S3Target(name, regionName, bucketVariable));
    }

    // General purpose bucket names (no dots: see above).
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BucketName();
}

/// <summary>An S3 client with its credentials resolved, and the opaque scope of those credentials.</summary>
/// <param name="CredentialScope">Equal for two clients signing as the same access key; never the key itself.</param>
internal sealed record S3Client(IAmazonS3 Api, string CredentialScope);

/// <summary>
/// The bucket and its client, shared by the stores of one bucket. The client and credentials are
/// made <b>once, on first use</b>: nothing touches credentials until a command needs storage, so
/// <c>--help</c>, <c>--version</c> and <c>self-check</c> never do.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Credentials:</b> the standard AWS chain (environment, <c>AWS_PROFILE</c> and the shared
/// files, SSO, web identity, ECS, EC2 instance roles), or one named profile. Resolved up front, so
/// "none found", an expired SSO session and the like are one clear error, not a failure mid-transfer.
/// With nothing configured the chain ends at the EC2 metadata endpoint, which fails within about a
/// second off EC2 (<c>AWS_EC2_METADATA_DISABLED=true</c> skips it).</item>
/// <item><b>Redirects are never followed:</b> a bucket in another region is a configuration error that
/// names the right region (<see cref="S3ErrorMapper"/>).</item>
/// <item>The SDK's clock-skew correction and integrity checksums stay on.</item>
/// </list>
/// </remarks>
internal sealed class S3Connection : IDisposable
{
    // One for the process, never disposed: refreshing credentials it hands out (SSO, instance roles) live as long.
    private static readonly DefaultAWSCredentialsIdentityResolver Resolver = new();

    private readonly Lazy<Result<S3Bucket>> _bucket;
    private readonly Func<S3Bucket, CancellationToken, Task<Result<S3Client>>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Result<S3Client>? _client;

    public S3Connection(Func<Result<S3Bucket>> bucket, string? profile, S3TransferSettings settings, ILogger? logger = null)
        : this(bucket, (target, token) => ConnectAsync(target, profile, settings, logger ?? NullLogger.Instance, token), settings)
    {
    }

    /// <summary>Tests: any client (e.g. a substitute <see cref="IAmazonS3"/>).</summary>
    internal S3Connection(Func<Result<S3Bucket>> bucket, Func<S3Bucket, CancellationToken, Task<Result<S3Client>>> connect, S3TransferSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        _bucket = new Lazy<Result<S3Bucket>>(bucket);
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public S3TransferSettings Settings { get; }

    public Result<S3Bucket> Bucket => _bucket.Value;

    /// <summary>The client, made the first time it's asked for; the outcome (success or the error) is kept for the process.</summary>
    public async Task<Result<S3Client>> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } ready)
        {
            return ready;
        }

        if (Bucket.IsFailure)
        {
            return Result.Failure<S3Client>(Bucket.Error);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _client ??= await _connect(Bucket.Value, cancellationToken);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_client is { IsSuccess: true } client)
        {
            client.Value.Api.Dispose();
        }

        _gate.Dispose();
    }

    /// <summary>The opaque credential scope: a short hash of the access key ID (never the key, and never the secret).</summary>
    internal static string ScopeOf(string accessKeyId) =>
        "aws:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyId)))[..16];

    private static async Task<Result<S3Client>> ConnectAsync(S3Bucket bucket, string? profile, S3TransferSettings settings, ILogger logger, CancellationToken cancellationToken)
    {
        AmazonS3Config config = new()
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(bucket.Region),
            MaxErrorRetry = settings.MaxRetries,
            Timeout = settings.Timeout,
            AllowAutoRedirect = false,
            RetryMode = RequestRetryMode.Standard,
        };

        AWSCredentials credentials;
        ImmutableCredentials resolved;
        try
        {
            Result<AWSCredentials> found = await ResolveAsync(profile, config, cancellationToken);
            if (found.IsFailure)
            {
                return Result.Failure<S3Client>(found.Error);
            }

            credentials = found.Value;
            resolved = await credentials.GetCredentialsAsync(); // SSO, assumed roles and instance roles fetch here
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            S3Log.CredentialsFailed(logger, S3Redactor.Redact(exception.GetType().Name + ": " + exception.Message));
            return Result.Failure<S3Client>(CredentialError(exception));
        }

        if (string.IsNullOrEmpty(resolved.AccessKey))
        {
            return Result.Failure<S3Client>(ConfigurationErrors.NoCredentials("AWS S3", S3ErrorMapper.CredentialSources));
        }

#pragma warning disable CA2000 // Owned by the S3Client returned, disposed with the connection.
        return new S3Client(new AmazonS3Client(credentials, config), ScopeOf(resolved.AccessKey));
#pragma warning restore CA2000
    }

    /// <summary>
    /// The credentials, in the order the <c>aws</c> CLI uses: a named profile (publishing's own);
    /// else keys in the environment; else the profile <c>AWS_PROFILE</c> names (or <c>default</c>), of
    /// any kind, SSO included (the SDK's default chain refuses SSO profiles); else the rest of the
    /// chain (web identity, ECS, the EC2 instance role).
    /// </summary>
    private static async Task<Result<AWSCredentials>> ResolveAsync(string? profile, AmazonS3Config config, CancellationToken cancellationToken)
    {
        CredentialProfileStoreChain profiles = new();
        if (profile is not null)
        {
            return profiles.TryGetAWSCredentials(profile, out AWSCredentials? named)
                ? Result.Success(named)
                : Result.Failure<AWSCredentials>(ConfigurationErrors.Invalid(
                    S3PublishOptions.ProfileVariable, $"there is no AWS profile '{profile}' in the shared config or credentials files."));
        }

        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(AccessKeyVariable)))
        {
            string? chosen = System.Environment.GetEnvironmentVariable(ProfileVariable);
            if (profiles.TryGetAWSCredentials(string.IsNullOrWhiteSpace(chosen) ? "default" : chosen.Trim(), out AWSCredentials? fromProfile))
            {
                return Result.Success(fromProfile);
            }

            if (!string.IsNullOrWhiteSpace(chosen))
            {
                return Result.Failure<AWSCredentials>(ConfigurationErrors.Invalid(
                    ProfileVariable, $"there is no AWS profile '{chosen.Trim()}' in the shared config or credentials files."));
            }
        }

        return Result.Success(await Resolver.ResolveIdentityAsync(config, cancellationToken));
    }

    private const string AccessKeyVariable = "AWS_ACCESS_KEY_ID";
    private const string ProfileVariable = "AWS_PROFILE";

    internal static Error CredentialError(Exception exception)
    {
        if (S3ErrorMapper.IsExpiredSession(exception))
        {
            return StorageErrors.UnauthorizedBecause(S3ErrorMapper.ExpiredHint);
        }

        if (S3ErrorMapper.IsMissingCredentials(exception))
        {
            return ConfigurationErrors.NoCredentials("AWS S3", S3ErrorMapper.CredentialSources);
        }

        // An SSO portal, STS or the instance metadata endpoint that can't be reached: the network, not the credentials.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return StorageErrors.Unreachable;
            }
        }

        return StorageErrors.UnauthorizedBecause("The AWS credentials couldn't be loaded; run with --verbose for the reason.");
    }
}
