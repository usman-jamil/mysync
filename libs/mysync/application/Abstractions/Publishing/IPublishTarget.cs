using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Abstractions.Publishing;

/// <summary>
/// Where publications live: a public, read-only place the provider serves anonymously (Azure: the
/// static website's <c>$web</c> container; S3: a publishing bucket). One implementation per
/// provider, chosen by <c>MYSYNC_PUBLISH_PROVIDER</c>. Blob names come from <c>PublicationLayout</c>.
/// Every method returns a <see cref="Result"/>; only the caller's own cancellation propagates.
/// </summary>
public interface IPublishTarget
{
    /// <summary>
    /// The account and public base URL, or the configuration error (a missing or malformed
    /// variable). Read on first use, so commands that don't publish never need the variables.
    /// </summary>
    Result<PublishLocation> GetLocation();

    /// <summary>
    /// The target is ready to serve publications, checked before anything is written (Azure: static
    /// website hosting is on, with <c>index.html</c> as the index document; S3: the bucket is in its
    /// region, serves a probe anonymously, and can't be listed anonymously). Never changes the
    /// account or bucket: that's its owner's decision.
    /// </summary>
    Task<Result> PreflightAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the share's account and this target's are one (the publication's URL then names the
    /// account that holds the private shares): same provider, and the same account (Azure) or bucket (S3).
    /// </summary>
    bool SharesAccountWith(StorageLocation shares);

    /// <summary>
    /// Whether <see cref="CopyAsync"/> can copy from <paramref name="source"/> without the bytes
    /// passing through this machine (a URL this provider can read from, or an object of its own
    /// provider reachable with its credentials). When it can't, the caller streams the object.
    /// </summary>
    bool CanCopy(CopySource source);

    /// <summary>
    /// Uploads <paramref name="content"/>. With <paramref name="onlyIfAbsent"/>, an existing blob
    /// is left alone and the result is <c>false</c> (<c>If-None-Match: *</c>).
    /// </summary>
    Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken);

    /// <summary>
    /// Copies <paramref name="source"/> server-side (only when <see cref="CanCopy"/> said so), setting
    /// the headers. <paramref name="length"/> is the source's stored length.
    /// </summary>
    Task<Result<CopyMethod>> CopyAsync(CopySource source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken);

    /// <summary>A missing blob counts as deleted.</summary>
    Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken);

    /// <summary>Deletes every blob whose name starts with <paramref name="prefix"/>; returns how many.</summary>
    Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken);
}

/// <param name="Provider">The publishing provider (<c>azure-blob</c>, <c>s3</c>).</param>
/// <param name="AccountName">Recorded with each publication (the Azure account; <c>s3://&lt;bucket&gt;</c> on S3), so <c>unpublish</c> refuses one that lives elsewhere.</param>
/// <param name="BaseUrl">The public URL (<c>MYSYNC_PUBLISH_BASE_URL</c>), absolute <c>https</c>.</param>
/// <param name="ServesIndexDocuments">
/// Whether <c>&lt;base&gt;/p/&lt;id&gt;/</c> serves its <c>index.html</c> (Azure's static website does; S3's
/// REST endpoint doesn't, so links there end in <c>index.html</c>).
/// </param>
/// <param name="NotServedStatusCodes">
/// The anonymous HTTP statuses that mean "not served" (missing, or not public): 404 on Azure; 403 and
/// 404 on S3, which answers 403 for a missing key to anyone who can't list the bucket.
/// </param>
public sealed record PublishLocation(string Provider, string AccountName, Uri BaseUrl, bool ServesIndexDocuments, IReadOnlySet<int> NotServedStatusCodes)
{
    /// <summary>Azure's static website: index documents served, 404 for what isn't there.</summary>
    public static PublishLocation StaticWebsite(string provider, string accountName, Uri baseUrl) =>
        new(provider, accountName, baseUrl, ServesIndexDocuments: true, new HashSet<int> { 404 });
}

/// <summary>
/// Where a server-side copy can read a stored object from, as the share's store offers it. At
/// least one of the two is set; the publish target picks what it can use (<see cref="IPublishTarget.CanCopy"/>).
/// </summary>
/// <param name="Url">A URL anyone holding it can read the object from for a short time (Azure: a read-only SAS; S3: a presigned GET).</param>
/// <param name="Native">The object in its provider's own terms, for a target of the same provider.</param>
public sealed record CopySource(Uri? Url, NativeObjectReference? Native = null);

/// <summary>A stored object named the way its provider names it.</summary>
/// <param name="Provider">The share's provider (<c>s3</c>).</param>
/// <param name="Region">Where the container is (S3: the bucket's region).</param>
/// <param name="Container">The bucket or container.</param>
/// <param name="Key">The object's key in it.</param>
/// <param name="CredentialScope">
/// Opaque: equal for two stores that use the same credentials. A target copies natively only from a
/// source with its own scope (the identity that can read the source is the one doing the copy).
/// </param>
public sealed record NativeObjectReference(string Provider, string Region, string Container, string Key, string CredentialScope);

/// <summary>The headers a published blob is served with (the static website sends whatever the blob has).</summary>
/// <param name="ContentEncoding">
/// <c>gzip</c> when the bytes are gzip (copied as stored from a compressed object), so browsers,
/// <c>curl --compressed</c> and PowerShell decode them; <c>null</c> for raw bytes.
/// </param>
public sealed record PublishedBlob(string ContentType, string? ContentDisposition = null, string? CacheControl = null, string? ContentEncoding = null)
{
    /// <summary>Published files never change: each publication is its own copy.</summary>
    public const string Immutable = "public, max-age=31536000, immutable";
}

public enum CopyMethod
{
    /// <summary>Azure, one request: Put Blob From URL (up to <c>AzureCopyLimits.PutBlobFromUrlMaxBytes</c>).</summary>
    PutBlobFromUrl,

    /// <summary>Blocks staged from the source URL, then one commit (larger files, or a service without Put Blob From URL).</summary>
    BlocksFromUrl,

    /// <summary>Downloaded and uploaded through this machine (no source the target can copy from: a credential that can't sign, or another provider).</summary>
    Streamed,

    /// <summary>S3, one request: CopyObject (up to 5 GiB), the headers replaced.</summary>
    CopyObject,

    /// <summary>S3, larger objects: a multipart upload whose parts are copied (UploadPartCopy).</summary>
    MultipartCopy,
}

/// <summary>Checks a publication from the outside, as anyone with the link would see it: anonymously.</summary>
public interface IPublicationVerifier
{
    /// <summary>
    /// Fetches <paramref name="siteDataUrl"/> without credentials and checks it names <paramref name="publishId"/>.
    /// A status in <paramref name="notServedStatusCodes"/> is reported as "not served" (missing, or not public).
    /// </summary>
    Task<VerificationOutcome> VerifyAsync(Uri siteDataUrl, string publishId, IReadOnlySet<int> notServedStatusCodes, CancellationToken cancellationToken);
}

/// <param name="Detail">Why it failed (an HTTP status, a network error), for the user.</param>
public sealed record VerificationOutcome(bool Verified, string? Detail = null);

/// <summary>Writes <c>site-data.js</c>: <c>window.__MYSYNC_SITE__ = {…};</c> (Infrastructure, source-generated JSON).</summary>
public interface ISiteDataSerializer
{
    byte[] Serialize(Domain.Publishing.SiteData data);
}
