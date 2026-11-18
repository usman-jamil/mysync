using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Publishing;
using SharedKernel;

namespace MySync.Application.Diagnostics.SiteAssets;

internal sealed class ListSiteAssetsQueryHandler(ISiteAssetSource assets, IContentHasher hasher)
    : IQueryHandler<ListSiteAssetsQuery, SiteAssetsResponse>
{
    public async Task<Result<SiteAssetsResponse>> Handle(ListSiteAssetsQuery query, CancellationToken cancellationToken)
    {
        List<SiteAssetInfo> listed = [];
        foreach ((string folder, IReadOnlyList<SiteAsset> group) in new[] { ("site", assets.PublicationAssets), ("root", assets.RootAssets) })
        {
            foreach (SiteAsset asset in group)
            {
                await using Stream content = assets.Open(asset);
                listed.Add(new SiteAssetInfo($"{folder}/{asset.Path}", asset.ContentType, asset.Length, await hasher.HashAsync(content, cancellationToken)));
            }
        }

        return Result.Success(new SiteAssetsResponse(assets.IsPlaceholder, assets.Version, listed));
    }
}
