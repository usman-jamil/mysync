using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Publishing;
using Shouldly;
using Xunit;

namespace MySync.SiteAssets.UnitTests;

/// <summary>
/// The assets this build embedded. Needs the real site build (<c>nx build mysync-site</c>): the
/// first test fails with that advice if the assembly carries the Debug placeholder instead.
/// </summary>
public sealed class EmbeddedSiteAssetSourceTests
{
    private readonly EmbeddedSiteAssetSource _source = new();

    [Fact]
    public void TheSiteBuild_Should_BeEmbedded_NotThePlaceholder() =>
        _source.IsPlaceholder.ShouldBeFalse(
            "MySync.SiteAssets was built without the site. Run \"nx build mysync-site\" and build again, or run this test through Nx.");

    [Fact]
    public void ThePublicationAssets_Should_BeExactlyTheSiteBuildOutput() =>
        _source.PublicationAssets.Select(asset => asset.Path)
            .ShouldBe(["assets/THIRD-PARTY-NOTICES.txt", "assets/app.css", "assets/app.js", "index.html"]);

    [Fact]
    public void TheRootAssets_Should_BeTheCliOwnedFiles() =>
        _source.RootAssets.Select(asset => asset.Path).ShouldBe(["404.html", "index.html", "robots.txt"]);

    [Fact]
    public void IndexHtml_Should_LoadTheDataFirst_ThenTheApp()
    {
        string html = Text(_source.PublicationAssets.Single(asset => asset.Path == "index.html"));

        html.ShouldContain("<script src=\"./site-data.js\"></script>");
        html.ShouldContain("src=\"./assets/app.js\"");
        html.ShouldContain("href=\"./assets/app.css\"");
        html.IndexOf("./site-data.js", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("./assets/app.js", StringComparison.Ordinal));
        html.ShouldContain("Content-Security-Policy");
    }

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("assets/app.js", "text/javascript; charset=utf-8")]
    [InlineData("assets/app.css", "text/css; charset=utf-8")]
    [InlineData("assets/THIRD-PARTY-NOTICES.txt", "text/plain; charset=utf-8")]
    public void EachAsset_Should_CarryTheContentTypeBrowsersNeed(string path, string contentType) =>
        _source.PublicationAssets.Single(asset => asset.Path == path).ContentType.ShouldBe(contentType);

    [Fact]
    public void EachAsset_Should_OpenToItsDeclaredLength()
    {
        foreach (SiteAsset asset in _source.PublicationAssets.Concat(_source.RootAssets))
        {
            Bytes(asset).Length.ShouldBe((int)asset.Length, asset.Path);
            asset.Length.ShouldBeGreaterThan(0, asset.Path);
        }
    }

    [Fact]
    public void TheRootFiles_Should_KeepCrawlersOut_AndRunNoScript()
    {
        Text(_source.RootAssets.Single(asset => asset.Path == "robots.txt")).ShouldBe("User-agent: *\nDisallow: /\n");
        foreach (SiteAsset page in _source.RootAssets.Where(asset => asset.Path.EndsWith(".html", StringComparison.Ordinal)))
        {
            string html = Text(page);
            html.ShouldNotContain("<script", Case.Insensitive);
            html.ShouldContain("noindex");
            html.ShouldContain("default-src 'none'");
        }
    }

    [Fact]
    public void TheVersion_Should_BeADigestOfTheAssets_StableAcrossInstances()
    {
        _source.Version.ShouldStartWith("sha256:");
        _source.Version.Length.ShouldBe("sha256:".Length + 16);
        new EmbeddedSiteAssetSource().Version.ShouldBe(_source.Version);
        new EmbeddedSiteAssetSource(typeof(EmbeddedSiteAssetSourceTests).Assembly).Version.ShouldNotBe(_source.Version);
    }

    [Fact]
    public void Open_Should_RefuseAnAssetItDoesNotHave() =>
        Should.Throw<ArgumentException>(() => _source.Open(new SiteAsset("../../secret", "text/plain", 1)));

    [Fact]
    public void ThePlaceholder_Should_BeRecognised()
    {
        var placeholder = new EmbeddedSiteAssetSource(typeof(EmbeddedSiteAssetSourceTests).Assembly);

        placeholder.IsPlaceholder.ShouldBeTrue();
        placeholder.PublicationAssets.Select(asset => asset.Path).ShouldBe(["index.html"]);
        placeholder.RootAssets.ShouldBeEmpty();
    }

    [Fact]
    public void AddSiteAssets_Should_RegisterOneSharedSource()
    {
        using ServiceProvider services = new ServiceCollection().AddSiteAssets().BuildServiceProvider();

        services.GetRequiredService<ISiteAssetSource>().ShouldBeSameAs(services.GetRequiredService<ISiteAssetSource>());
    }

    [Fact]
    public void TheEmbeddedBytes_Should_MatchTheSiteBuildOnDisk_WhenItIsThere()
    {
        string? dist = FindDist();
        Assert.SkipWhen(dist is null, "dist/apps/mysync-site isn't on disk here.");

        foreach (SiteAsset asset in _source.PublicationAssets)
        {
            SHA256.HashData(Bytes(asset)).ShouldBe(SHA256.HashData(File.ReadAllBytes(Path.Combine(dist!, asset.Path))), asset.Path);
        }
    }

    private byte[] Bytes(SiteAsset asset)
    {
        using Stream stream = _source.Open(asset);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private string Text(SiteAsset asset) => Encoding.UTF8.GetString(Bytes(asset));

    private static string? FindDist()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "mysync.slnx")))
            {
                string dist = Path.Combine(folder.FullName, "dist", "apps", "mysync-site");
                return Directory.Exists(dist) ? dist : null;
            }
        }

        return null;
    }
}
