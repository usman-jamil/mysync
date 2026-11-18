using System.Security.Cryptography;
using System.Text;
using MySync.Application.Diagnostics.SiteAssets;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Diagnostics;

public class ListSiteAssetsQueryHandlerTests
{
    [Fact]
    public async Task Handle_Should_ListEveryAssetWithItsHash_PublicationFirst()
    {
        var handler = new ListSiteAssetsQueryHandler(new InMemorySiteAssetSource(), new TestContentHasher());

        Result<SiteAssetsResponse> result = await handler.Handle(new ListSiteAssetsQuery(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsPlaceholder.ShouldBeFalse();
        result.Value.Version.ShouldBe("sha256:test000000000000");
        result.Value.Assets.Select(asset => asset.Path).ShouldBe(
        [
            "site/assets/THIRD-PARTY-NOTICES.txt", "site/assets/app.css", "site/assets/app.js", "site/index.html",
            "root/404.html", "root/index.html", "root/robots.txt",
        ]);
        SiteAssetInfo robots = result.Value.Assets.Single(asset => asset.Path == "root/robots.txt");
        robots.Sha256.Value.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /\n"))));
        robots.ContentType.ShouldBe("text/plain; charset=utf-8");
    }

    [Fact]
    public async Task Handle_Should_SayWhenTheAssetsAreThePlaceholder()
    {
        var handler = new ListSiteAssetsQueryHandler(new InMemorySiteAssetSource(isPlaceholder: true), new TestContentHasher());

        Result<SiteAssetsResponse> result = await handler.Handle(new ListSiteAssetsQuery(), TestContext.Current.CancellationToken);

        result.Value.IsPlaceholder.ShouldBeTrue();
    }
}
