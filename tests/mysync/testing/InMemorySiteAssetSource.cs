using System.Text;
using MySync.Application.Abstractions.Publishing;

namespace MySync.Testing;

/// <summary>
/// Site assets held in memory, for tests that need an <see cref="ISiteAssetSource"/> without the
/// real site build. Defaults to a minimal, non-placeholder set shaped like the real one.
/// </summary>
public sealed class InMemorySiteAssetSource : ISiteAssetSource
{
    private readonly Dictionary<SiteAsset, byte[]> _contents = [];

    public InMemorySiteAssetSource(bool isPlaceholder = false)
    {
        IsPlaceholder = isPlaceholder;
        PublicationAssets =
        [
            Add("assets/THIRD-PARTY-NOTICES.txt", "text/plain; charset=utf-8", "notices"),
            Add("assets/app.css", "text/css; charset=utf-8", "body{}"),
            Add("assets/app.js", "text/javascript; charset=utf-8", "console.log('app')"),
            Add("index.html", "text/html; charset=utf-8", "<!doctype html><script src=\"./site-data.js\"></script><script type=\"module\" src=\"./assets/app.js\"></script>"),
        ];
        RootAssets =
        [
            Add("404.html", "text/html; charset=utf-8", "<!doctype html><title>Not found</title>"),
            Add("index.html", "text/html; charset=utf-8", "<!doctype html><title>Nothing here</title>"),
            Add("robots.txt", "text/plain; charset=utf-8", "User-agent: *\nDisallow: /\n"),
        ];
    }

    public bool IsPlaceholder { get; }

    public string Version => IsPlaceholder ? "placeholder" : "sha256:test000000000000";

    public IReadOnlyList<SiteAsset> PublicationAssets { get; }

    public IReadOnlyList<SiteAsset> RootAssets { get; }

    public Stream Open(SiteAsset asset) =>
        _contents.TryGetValue(asset, out byte[]? bytes)
            ? new MemoryStream(bytes, writable: false)
            : throw new ArgumentException($"{asset?.Path} is not one of these site assets.", nameof(asset));

    private SiteAsset Add(string path, string contentType, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        SiteAsset asset = new(path, contentType, bytes.Length);
        _contents[asset] = bytes;
        return asset;
    }
}
