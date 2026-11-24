using SharedKernel;

namespace MySync.Domain.Publishing;

/// <summary>Why <c>publish</c>, <c>publications</c> or <c>unpublish</c> stopped. No description ever holds a connection string.</summary>
public static class PublishErrors
{
    public const string PublishConnectionStringVariable = "MYSYNC_PUBLISH_CONNECTION_STRING";
    public const string PublishBaseUrlVariable = "MYSYNC_PUBLISH_BASE_URL";

    private static string EnableCommand(string account) =>
        $"az storage blob service-properties update --account-name {account} --static-website --index-document index.html --404-document 404.html";

    public static Error StaticWebsiteDisabled(string account) => Error.Validation(
        "Publish.StaticWebsiteDisabled",
        $"Static website hosting is off on the publishing account '{account}'. Turn it on with:\n  {EnableCommand(account)}");

    public static Error IndexDocumentMismatch(string account, string? actual) => Error.Validation(
        "Publish.IndexDocumentMismatch",
        $"The publishing account '{account}' serves '{actual ?? "(none)"}' as its index document; mysync needs index.html. Fix it with:\n  {EnableCommand(account)}");

    public static Error CannotReadServiceProperties(string account) => Error.Problem(
        "Publish.CannotReadServiceProperties",
        $"MYSYNC_PUBLISH_CONNECTION_STRING can't read the service properties of '{account}', so mysync can't check that static website hosting is on. Use the account key (or a SAS with service-level read access).");

    public static readonly Error Unauthorized = Error.Problem(
        "Publish.Unauthorized", "The publishing account rejected the credentials. Check MYSYNC_PUBLISH_CONNECTION_STRING.");

    /// <summary>The publishing storage rejected the credentials; <paramref name="hint"/> says what to check for that provider.</summary>
    public static Error UnauthorizedBecause(string hint) => Error.Problem(
        "Publish.Unauthorized", $"The publishing storage rejected the request. {hint}");

    public static readonly Error SiteAssetsMissing = Error.Problem(
        "Publish.SiteAssetsMissing",
        "This mysync was built without its publication page (a development build). Build it with \"nx build mysync\" to publish.");

    public static Error InvalidId() => Error.Validation(
        "Publish.InvalidId", "A publication ID is 22 letters and digits, as `mysync publications` lists them.");

    public static Error InvalidTitle() => Error.Validation(
        "Publish.InvalidTitle", "A title is 1 to 200 characters, without control characters.");

    public static Error NotFound(PublishId id) => Error.NotFound(
        "Publish.NotFound", $"This share has no publication {id}. `mysync publications` lists them.");

    public static Error RevisionNotFound(long revision) => Error.NotFound(
        "Publish.RevisionNotFound", $"Revision {revision} isn't in the share's history (it may have been removed by gc).");

    public static Error OtherAccount(PublishId id, string recorded, string configured) => Error.Validation(
        "Publish.OtherAccount",
        $"Publication {id} lives in the publishing account '{recorded}', but the publishing settings (MYSYNC_PUBLISH_PROVIDER and its variables) point at '{configured}'.");

    /// <summary>The share has publications, and the publishing settings needed to remove them aren't set (<paramref name="missing"/> says which).</summary>
    public static Error PublicationsNeedSettings(int count, Error missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return Error.Validation(
            "Publish.PublicationsNeedSettings",
            $"This share has {count} publication(s), and `mysync delete` removes them too, so it needs the publishing settings: {missing.Description} Otherwise their public copies would be left behind with no record of them.");
    }

    /// <summary>
    /// S3: the probe written to the publishing bucket wasn't readable anonymously through the base
    /// URL. mysync can't tell which of the three blocks it, so it names them all.
    /// </summary>
    public static Error NotPublic(string bucket, string baseUrl) => Error.Validation(
        "Publish.NotPublic",
        $"The publishing bucket '{bucket}' doesn't serve files to anonymous readers at {baseUrl}, so a publication's link wouldn't open. " +
        $"Allow public reads of p/* and robots.txt (and nothing else) with this bucket policy:\n{PublicReadPolicy(bucket)}\n" +
        "Then check the three things that can still block it: the bucket's Block Public Access settings (BlockPublicPolicy and RestrictPublicBuckets off), " +
        "the account-level Block Public Access settings (`aws s3control get-public-access-block`), and an organisation policy (SCP). " +
        "If MYSYNC_PUBLISH_BASE_URL is a CloudFront distribution, check that it serves this bucket.");

    /// <summary>S3: an anonymous listing of the publishing bucket succeeded; it would reveal every publication ID, the only secret of a link.</summary>
    public static Error PublicListing(string bucket) => Error.Validation(
        "Publish.PublicListing",
        $"Anyone can list the publishing bucket '{bucket}', which would reveal every publication's ID (the only thing that keeps a link private). " +
        "Remove s3:ListBucket for the public (\"Principal\": \"*\") from its bucket policy; mysync publishes nothing until it can't be listed.");

    /// <summary>The bucket policy that makes publications readable and nothing else.</summary>
    public static string PublicReadPolicy(string bucket) =>
        "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"PublicReadPublished\",\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\"," +
        $"\"Resource\":[\"arn:aws:s3:::{bucket}/p/*\",\"arn:aws:s3:::{bucket}/robots.txt\"]}}]}}";

    public static Error RegistryRetriesExhausted(int attempts) => Error.Conflict(
        "Publish.RegistryRetriesExhausted", $"publications.json kept changing under us; gave up after {attempts} attempts. Try again.");

    public static Error InvalidRegistry(string detail) => Error.Problem(
        "Publish.InvalidRegistry", $"The share's publications.json can't be read: {detail}");
}
