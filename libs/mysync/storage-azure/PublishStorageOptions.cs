namespace MySync.Storage.Azure;

/// <summary>
/// The publishing account (<c>mysync publish</c>), from environment variables only. Not validated
/// at startup: only the commands that publish need these, and they report a missing or malformed
/// one as a configuration error (exit 2).
/// </summary>
public sealed class PublishStorageOptions
{
    public const string ConnectionStringVariable = "MYSYNC_PUBLISH_CONNECTION_STRING";
    public const string BaseUrlVariable = "MYSYNC_PUBLISH_BASE_URL";

    /// <summary>A secret. Never print, log or store it.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The account's static website endpoint (or a custom domain in front of it), <c>https</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Keeps the connection string out of anything that formats this object.</summary>
    public override string ToString() => $"{nameof(PublishStorageOptions)} {{ BaseUrl = {BaseUrl} }}";
}
