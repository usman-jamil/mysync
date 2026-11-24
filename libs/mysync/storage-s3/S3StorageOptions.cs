namespace MySync.Storage.S3;

/// <summary>
/// The share bucket, bound from environment variables only (CLAUDE.md §5). Not validated at
/// startup, so <c>mysync --help</c> works with nothing configured; a missing or malformed value
/// surfaces as a <c>ConfigurationErrors</c> result when a command first needs storage. Credentials
/// are not here: they come from the standard AWS chain, and mysync never stores them.
/// </summary>
public sealed class S3StorageOptions
{
    public const string BucketVariable = "MYSYNC_S3_BUCKET";
    public const string RegionVariable = "MYSYNC_S3_REGION";

    /// <summary>The AWS CLI's own region variable, used when <see cref="RegionVariable"/> isn't set.</summary>
    public const string AwsRegionVariable = "AWS_REGION";

    public string? Bucket { get; set; }

    public string? Region { get; set; }

    /// <summary>
    /// <b>Tests only.</b> Every key starts with it, so test runs sharing one real bucket don't see
    /// each other's names and can delete what they wrote. Never bound from the environment: in
    /// production keys are exactly what <c>ShareLayout</c> builds.
    /// </summary>
    internal string KeyPrefix { get; set; } = string.Empty;

    public override string ToString() => $"{nameof(S3StorageOptions)} {{ Bucket = {Bucket}, Region = {Region} }}";
}

/// <summary>
/// The publishing bucket (<c>mysync publish</c> with <c>MYSYNC_PUBLISH_PROVIDER=s3</c>), from
/// environment variables only. Read only by the commands that publish.
/// </summary>
public sealed class S3PublishOptions
{
    public const string BucketVariable = "MYSYNC_PUBLISH_S3_BUCKET";
    public const string RegionVariable = "MYSYNC_PUBLISH_S3_REGION";
    public const string ProfileVariable = "MYSYNC_PUBLISH_AWS_PROFILE";
    public const string BaseUrlVariable = "MYSYNC_PUBLISH_BASE_URL";

    public string? Bucket { get; set; }

    /// <summary><see cref="RegionVariable"/>, else the share's region (<c>MYSYNC_S3_REGION</c>, then <c>AWS_REGION</c>).</summary>
    public string? Region { get; set; }

    /// <summary>
    /// A credential profile for publishing only; <c>null</c> for the same credentials as the share
    /// bucket. With another profile, copies are streamed through this machine: that identity may not
    /// be able to read the share bucket.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>The bucket's public HTTPS address (its REST endpoint, or CloudFront in front of it).</summary>
    public string? BaseUrl { get; set; }

    public override string ToString() => $"{nameof(S3PublishOptions)} {{ Bucket = {Bucket}, Region = {Region}, BaseUrl = {BaseUrl} }}";
}
