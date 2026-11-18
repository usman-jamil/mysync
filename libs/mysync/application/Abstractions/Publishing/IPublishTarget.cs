using SharedKernel;

namespace MySync.Application.Abstractions.Publishing;

/// <summary>
/// Where publications live: a static website (<c>$web</c>) on the publishing storage account
/// (<c>MYSYNC_PUBLISH_CONNECTION_STRING</c>). Blob names come from <c>PublicationLayout</c>.
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
    /// Static website hosting is on, with <c>index.html</c> as the index document. Never turns it
    /// on: that's the account owner's decision.
    /// </summary>
    Task<Result> CheckStaticWebsiteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Uploads <paramref name="content"/>. With <paramref name="onlyIfAbsent"/>, an existing blob
    /// is left alone and the result is <c>false</c> (<c>If-None-Match: *</c>).
    /// </summary>
    Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken);

    /// <summary>Copies <paramref name="source"/> (a readable URL, e.g. with a short-lived SAS) server-side, setting the headers.</summary>
    Task<Result<CopyMethod>> CopyAsync(Uri source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken);

    /// <summary>A missing blob counts as deleted.</summary>
    Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken);

    /// <summary>Deletes every blob whose name starts with <paramref name="prefix"/>; returns how many.</summary>
    Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken);
}

/// <param name="AccountName">Recorded with each publication; compared with the private account's.</param>
/// <param name="BaseUrl">The static website's public URL (<c>MYSYNC_PUBLISH_BASE_URL</c>), absolute <c>https</c>.</param>
public sealed record PublishLocation(string AccountName, Uri BaseUrl);

/// <summary>The headers a published blob is served with (the static website sends whatever the blob has).</summary>
public sealed record PublishedBlob(string ContentType, string? ContentDisposition = null, string? CacheControl = null)
{
    /// <summary>Published files never change: each publication is its own copy.</summary>
    public const string Immutable = "public, max-age=31536000, immutable";
}

public enum CopyMethod
{
    /// <summary>One request: Put Blob From URL (up to <c>AzureCopyLimits.PutBlobFromUrlMaxBytes</c>).</summary>
    PutBlobFromUrl,

    /// <summary>Blocks staged from the source URL, then one commit (larger files, or a service without Put Blob From URL).</summary>
    BlocksFromUrl,

    /// <summary>Downloaded and uploaded through this machine (the private credential can't make a readable URL).</summary>
    Streamed,
}

/// <summary>Checks a publication from the outside, as anyone with the link would see it: anonymously.</summary>
public interface IPublicationVerifier
{
    /// <summary>Fetches <paramref name="siteDataUrl"/> without credentials and checks it names <paramref name="publishId"/>.</summary>
    Task<VerificationOutcome> VerifyAsync(Uri siteDataUrl, string publishId, CancellationToken cancellationToken);
}

/// <param name="Detail">Why it failed (an HTTP status, a network error), for the user.</param>
public sealed record VerificationOutcome(bool Verified, string? Detail = null);

/// <summary>Writes <c>site-data.js</c>: <c>window.__MYSYNC_SITE__ = {…};</c> (Infrastructure, source-generated JSON).</summary>
public interface ISiteDataSerializer
{
    byte[] Serialize(Domain.Publishing.SiteData data);
}
