using MySync.Domain.Paths;

namespace MySync.Domain.Publishing;

/// <summary>
/// Blob names in the publishing account's <c>$web</c> container: the one place they're composed.
/// </summary>
/// <example>
/// <code>
/// $web/index.html, 404.html, robots.txt          (the root: CLI-owned, shared by every publication)
/// $web/p/&lt;publishId&gt;/index.html                    (uploaded last: the commit point)
/// $web/p/&lt;publishId&gt;/assets/app.js, app.css, THIRD-PARTY-NOTICES.txt
/// $web/p/&lt;publishId&gt;/site-data.js, SHA256SUMS.txt, download.sh, download.ps1
/// $web/p/&lt;publishId&gt;/files/&lt;the file's path in the share&gt;
/// </code>
/// </example>
public sealed class PublicationLayout
{
    public const string SiteData = "site-data.js";
    public const string Sha256Sums = "SHA256SUMS.txt";
    public const string ShellScript = "download.sh";
    public const string PowerShellScript = "download.ps1";
    public const string IndexDocument = "index.html";

    /// <summary>Azure's limit on a blob name.</summary>
    public const int MaxBlobNameLength = 1024;

    private const char PathSeparator = '/';

    private PublicationLayout(PublishId id)
    {
        Id = id;
        Prefix = $"p/{id}/";
    }

    public PublishId Id { get; }

    /// <summary><c>p/&lt;publishId&gt;/</c>: everything of this publication, and nothing else.</summary>
    public string Prefix { get; }

    /// <summary>Uploaded last and deleted first: until it exists, the link shows nothing.</summary>
    public string Index => Prefix + IndexDocument;

    public static PublicationLayout For(PublishId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new PublicationLayout(id);
    }

    /// <summary>A file of the publication folder itself (site asset, site data, checksums, scripts).</summary>
    public string Blob(string relativePath) => Prefix + relativePath;

    public string File(SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Prefix + "files/" + path.Value;
    }

    /// <summary>
    /// The publication folder's public URL, <c>&lt;base&gt;/p/&lt;publishId&gt;/</c>: what every file of
    /// the publication is relative to (the download scripts, <c>site-data.js</c>).
    /// </summary>
    public Uri FolderUrl(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        // A base without a trailing slash would make Prefix replace its last segment.
        Uri root = baseUrl.AbsoluteUri.EndsWith(PathSeparator) ? baseUrl : new Uri(baseUrl.AbsoluteUri + PathSeparator);
        return new Uri(root, Prefix);
    }

    /// <summary>
    /// The link people open: the folder where the host serves index documents (Azure's static
    /// website), else the folder's <c>index.html</c> (S3's REST endpoint serves no index documents).
    /// </summary>
    public Uri Url(Uri baseUrl, bool servesIndexDocuments = true)
    {
        Uri folder = FolderUrl(baseUrl);
        return servesIndexDocuments ? folder : new Uri(folder, IndexDocument);
    }

    /// <summary>
    /// Why a share path can't be published as a blob, or <c>null</c> if it can. Azure drops a
    /// trailing dot from a blob name, and limits names to 1024 characters.
    /// </summary>
    public string? CannotPublish(SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Value.EndsWith('.'))
        {
            return "a name ending in '.' can't be a blob name";
        }

        return File(path).Length > MaxBlobNameLength ? $"the path is longer than a blob name allows ({MaxBlobNameLength} characters)" : null;
    }
}
