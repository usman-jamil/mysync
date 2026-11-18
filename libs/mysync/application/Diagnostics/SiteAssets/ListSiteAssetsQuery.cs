using MySync.Application.Abstractions.Messaging;
using MySync.Domain.Content;

namespace MySync.Application.Diagnostics.SiteAssets;

/// <summary>What publication assets this binary carries (the hidden <c>mysync debug site-assets</c>, a release smoke test).</summary>
public sealed record ListSiteAssetsQuery : IQuery<SiteAssetsResponse>;

public sealed record SiteAssetsResponse(bool IsPlaceholder, string Version, IReadOnlyList<SiteAssetInfo> Assets);

/// <param name="Path">Prefixed with its folder: <c>site/assets/app.js</c> or <c>root/robots.txt</c>.</param>
public sealed record SiteAssetInfo(string Path, string ContentType, long Length, ContentHash Sha256);
