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
        $"Publication {id} lives in the publishing account '{recorded}', but MYSYNC_PUBLISH_CONNECTION_STRING points at '{configured}'.");

    /// <summary>The share has publications, and the publishing settings needed to remove them aren't set.</summary>
    public static Error PublicationsNeedSettings(int count, string variable) => Error.Validation(
        "Publish.PublicationsNeedSettings",
        $"This share has {count} publication(s). Set {variable} (and the other publishing variable) so `mysync delete` can remove them too; otherwise their public copies would be left behind with no record of them.");

    public static Error RegistryRetriesExhausted(int attempts) => Error.Conflict(
        "Publish.RegistryRetriesExhausted", $"publications.json kept changing under us; gave up after {attempts} attempts. Try again.");

    public static Error InvalidRegistry(string detail) => Error.Problem(
        "Publish.InvalidRegistry", $"The share's publications.json can't be read: {detail}");
}
