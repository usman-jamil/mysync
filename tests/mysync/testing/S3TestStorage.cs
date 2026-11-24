using System.Globalization;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace MySync.Testing;

/// <summary>
/// A real, test-only S3 bucket (and optionally a public publishing bucket). Credentials come from
/// the standard AWS chain, as for mysync itself: in CI, <c>AWS_ACCESS_KEY_ID</c> and
/// <c>AWS_SECRET_ACCESS_KEY</c> from repository secrets, for an IAM user with exactly the
/// permissions of the setup guide (docs/aws.md), so a missing permission shows up here.
/// </summary>
/// <remarks>
/// Buckets can't be made per test (the documented policy has no <c>s3:CreateBucket</c>), so each test
/// isolates itself with its own key prefix (<see cref="NewKeyPrefix"/>, under this run's prefix).
/// mysync's stores take it as a tests-only option, so the keys under it are exactly
/// <c>ShareLayout</c>'s. <see cref="CleanUpAsync"/> deletes everything under the run's prefix;
/// give the bucket a lifecycle rule that expires objects after a day as well, for runs that die.
/// </remarks>
public sealed class S3TestStorage
{
    public const string BucketVariable = "MYSYNC_TEST_S3_BUCKET";
    public const string RegionVariable = "MYSYNC_TEST_S3_REGION";

    /// <summary>A public publishing bucket (bucket policy: anonymous s3:GetObject on p/* and robots.txt only).</summary>
    public const string PublishBucketVariable = "MYSYNC_TEST_S3_PUBLISH_BUCKET";

    /// <summary>That bucket's HTTPS address, e.g. <c>https://&lt;bucket&gt;.s3.&lt;region&gt;.amazonaws.com</c>.</summary>
    public const string PublishBaseUrlVariable = "MYSYNC_TEST_S3_PUBLISH_BASE_URL";

    private readonly string? _bucket;
    private readonly string? _region;
    private readonly string? _publishBucket;
    private readonly string? _publishBaseUrl;

    private S3TestStorage(string? bucket, string? region, string? publishBucket, string? publishBaseUrl)
    {
        _bucket = bucket;
        _region = region;
        _publishBucket = publishBucket;
        _publishBaseUrl = publishBaseUrl;
    }

    public static S3TestStorage FromEnvironment() => new(
        TestStorage.Variable(BucketVariable),
        TestStorage.Variable(RegionVariable) ?? TestStorage.Variable("AWS_REGION"),
        TestStorage.Variable(PublishBucketVariable),
        TestStorage.Variable(PublishBaseUrlVariable)?.TrimEnd('/'));

    public bool IsConfigured => _bucket is not null && _region is not null;

    public bool HasPublishing => IsConfigured && _publishBucket is not null && _publishBaseUrl is not null;

    public string Bucket => IsConfigured ? _bucket! : TestStorage.Unavailable<string>("An S3 test bucket", BucketVariable, RegionVariable, "AWS credentials (e.g. AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY)");

    public string Region => IsConfigured ? _region! : TestStorage.Unavailable<string>("An S3 test bucket", BucketVariable, RegionVariable);

    public string PublishBucket => HasPublishing ? _publishBucket! : TestStorage.Unavailable<string>("An S3 publishing bucket", PublishBucketVariable, PublishBaseUrlVariable);

    public string PublishBaseUrl => HasPublishing ? _publishBaseUrl! : TestStorage.Unavailable<string>("An S3 publishing bucket", PublishBucketVariable, PublishBaseUrlVariable);

    /// <summary>This run's prefix in the bucket: <c>t-&lt;8 hex&gt;/</c>.</summary>
    public static string RunKeyPrefix { get; } = TestStorage.RunPrefix.TrimEnd('-') + "/";

    /// <summary>A fresh, isolated place in the bucket for one test: <c>t-&lt;run&gt;/&lt;24 hex&gt;/</c>.</summary>
    public static string NewKeyPrefix() => RunKeyPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24] + "/";

    /// <summary>A client for looking at the bucket directly (the AWS chain's credentials).</summary>
    public AmazonS3Client Client() => new(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(Region) });

    /// <summary>Every key under <paramref name="prefix"/>.</summary>
    public async Task<List<S3Object>> ListAsync(string prefix, string? bucket = null)
    {
        using AmazonS3Client client = Client();
        List<S3Object> found = [];
        ListObjectsV2Request request = new() { BucketName = bucket ?? Bucket, Prefix = prefix };
        while (true)
        {
            ListObjectsV2Response page = await client.ListObjectsV2Async(request, CancellationToken.None);
            found.AddRange(page.S3Objects ?? []);
            if (page.IsTruncated != true)
            {
                return found;
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    /// <summary>Deletes everything under this run's prefix, and publication objects written since it started.</summary>
    public async Task CleanUpAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        await DeleteAsync(_bucket!, await ListAsync(RunKeyPrefix));
        if (_publishBucket is not null)
        {
            List<S3Object> published = await ListAsync("p/", _publishBucket);
            await DeleteAsync(_publishBucket, [.. published.Where(item => item.LastModified is { } modified && modified >= TestStorage.RunStartedUtc.UtcDateTime)]);
        }
    }

    /// <summary>Deletes these keys (a test's own leftovers outside the run prefix, e.g. a CLI run's share).</summary>
    public async Task DeleteAsync(string bucket, IReadOnlyCollection<S3Object> items)
    {
        using AmazonS3Client client = Client();
        foreach (S3Object[] batch in items.Chunk(1000))
        {
            await client.DeleteObjectsAsync(
                new DeleteObjectsRequest { BucketName = bucket, Objects = [.. batch.Select(item => new KeyVersion { Key = item.Key })], Quiet = true },
                CancellationToken.None);
        }
    }
}
