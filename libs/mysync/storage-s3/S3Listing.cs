using System.Runtime.CompilerServices;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SharedKernel;

namespace MySync.Storage.S3;

/// <summary>ListObjectsV2 under a prefix, page by page (S3 returns up to 1,000 keys per page).</summary>
internal static class S3Listing
{
    /// <summary>
    /// The bucket exists and the client's region is its region: a one-key listing, which needs only
    /// <c>s3:ListBucket</c>. From another region S3 answers it with a redirect naming the bucket's
    /// region (<c>Configuration.WrongRegion</c>), for a missing bucket with NoSuchBucket. Not
    /// GetBucketLocation: signed for the wrong region, S3 answers that one with access denied.
    /// Throws what the SDK throws; the caller maps it.
    /// </summary>
    public static Task CheckBucketAsync(S3Client client, S3Bucket bucket, CancellationToken cancellationToken) =>
        client.Api.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket.Name, Prefix = bucket.Key(string.Empty), MaxKeys = 1 }, cancellationToken);

    /// <param name="prefix">A layout prefix (the bucket's test prefix is added here).</param>
    /// <param name="pageSize">Keys per page; tests make it small to cross pages. S3's default (1,000) otherwise.</param>
    public static async IAsyncEnumerable<Result<S3Object>> ListAsync(
        S3Connection connection, string prefix, int? pageSize, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Result<S3Client> client = await connection.ClientAsync(cancellationToken);
        if (client.IsFailure)
        {
            yield return Result.Failure<S3Object>(client.Error);
            yield break;
        }

        S3Bucket bucket = connection.Bucket.Value;
        ListObjectsV2Request request = new() { BucketName = bucket.Name, Prefix = bucket.Key(prefix) };
        if (pageSize is { } size)
        {
            request.MaxKeys = size;
        }

        while (true)
        {
            Result<ListObjectsV2Response> page = await NextAsync(client.Value, request, bucket, prefix, logger, cancellationToken);
            if (page.IsFailure)
            {
                yield return Result.Failure<S3Object>(page.Error);
                yield break;
            }

            foreach (S3Object item in page.Value.S3Objects ?? [])
            {
                yield return item;
            }

            if (page.Value.IsTruncated != true || string.IsNullOrEmpty(page.Value.NextContinuationToken))
            {
                yield break;
            }

            request.ContinuationToken = page.Value.NextContinuationToken;
        }
    }

    private static async Task<Result<ListObjectsV2Response>> NextAsync(
        S3Client client, ListObjectsV2Request request, S3Bucket bucket, string prefix, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await client.Api.ListObjectsV2Async(request, cancellationToken);
        }
        catch (Exception exception) when (S3ErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<ListObjectsV2Response>(S3Calls.Failed(logger, exception, prefix, S3Operation.List, bucket.Target));
        }
    }
}

/// <summary>What every S3 call does with a failure: log it (redacted, debug) and map it.</summary>
internal static class S3Calls
{
    public static Error Failed(ILogger logger, Exception exception, string name, S3Operation operation, S3Target target)
    {
        S3Log.RequestFailed(logger, name, S3ErrorMapper.StatusOf(exception), S3ErrorMapper.CodeOf(exception), S3Redactor.Redact(exception.Message));
        return S3ErrorMapper.Map(exception, name, operation, target);
    }
}
