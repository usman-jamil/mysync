namespace MySync.Application.Abstractions.Publishing;

/// <summary>
/// The publication page's files, built from <c>apps/mysync-site</c> and carried inside the mysync
/// binary (<c>MySync.SiteAssets</c>), plus the plain files for the root of the static website.
/// </summary>
public interface ISiteAssetSource
{
    /// <summary>
    /// True when the binary was built without the site (a Debug build before <c>nx build mysync-site</c>):
    /// the assets are a placeholder page, and <c>mysync publish</c> refuses to run.
    /// </summary>
    bool IsPlaceholder { get; }

    /// <summary>Identifies this set of assets (a digest of their contents), recorded with each publication.</summary>
    string Version { get; }

    /// <summary>The files every publication folder gets: <c>index.html</c>, <c>assets/app.js</c>, …</summary>
    IReadOnlyList<SiteAsset> PublicationAssets { get; }

    /// <summary>The files at the root of the static website: <c>index.html</c>, <c>404.html</c>, <c>robots.txt</c>.</summary>
    IReadOnlyList<SiteAsset> RootAssets { get; }

    Stream Open(SiteAsset asset);
}

/// <param name="Path">Relative to its folder, with forward slashes (<c>assets/app.js</c>).</param>
/// <param name="ContentType">What the static website must serve it as; browsers refuse a module script with the wrong one.</param>
public sealed record SiteAsset(string Path, string ContentType, long Length);
