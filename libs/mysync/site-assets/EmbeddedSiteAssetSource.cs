using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MySync.Application.Abstractions.Publishing;

namespace MySync.SiteAssets;

/// <summary>
/// The site assets embedded in this assembly at build time (<c>MySync.SiteAssets.csproj</c>):
/// <c>site/…</c> for each publication folder, <c>root/…</c> for the root of the static website.
/// <see cref="Assembly.GetManifestResourceStream(string)"/> works in a single-file publish too.
/// </summary>
internal sealed class EmbeddedSiteAssetSource : ISiteAssetSource
{
    internal const string PlaceholderResource = "mysync-site-placeholder";
    private const string SitePrefix = "site/";
    private const string RootPrefix = "root/";

    private readonly Assembly _assembly;
    private readonly Lazy<string> _version;

    public EmbeddedSiteAssetSource()
        : this(typeof(EmbeddedSiteAssetSource).Assembly)
    {
    }

    internal EmbeddedSiteAssetSource(Assembly assembly)
    {
        _assembly = assembly;
        string[] names = assembly.GetManifestResourceNames();
        IsPlaceholder = names.Contains(PlaceholderResource, StringComparer.Ordinal);
        PublicationAssets = List(names, SitePrefix);
        RootAssets = List(names, RootPrefix);
        _version = new Lazy<string>(ComputeVersion);
    }

    public bool IsPlaceholder { get; }

    public string Version => _version.Value;

    public IReadOnlyList<SiteAsset> PublicationAssets { get; }

    public IReadOnlyList<SiteAsset> RootAssets { get; }

    public Stream Open(SiteAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (PublicationAssets.Contains(asset))
        {
            return Resource(SitePrefix + asset.Path);
        }

        return RootAssets.Contains(asset)
            ? Resource(RootPrefix + asset.Path)
            : throw new ArgumentException($"{asset.Path} is not one of the embedded site assets.", nameof(asset));
    }

    /// <summary>The content type the static website must serve each kind of asset with.</summary>
    internal static string ContentTypeOf(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".HTML" => "text/html; charset=utf-8",
        ".JS" => "text/javascript; charset=utf-8",
        ".CSS" => "text/css; charset=utf-8",
        ".TXT" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private List<SiteAsset> List(string[] names, string prefix)
    {
        List<SiteAsset> assets = [];
        foreach (string name in names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = Resource(name);
            string path = name[prefix.Length..];
            assets.Add(new SiteAsset(path, ContentTypeOf(path), stream.Length));
        }

        return assets;
    }

    private Stream Resource(string name) =>
        _assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");

    // SHA-256 over every publication asset's path and bytes, so equal assets give equal versions.
    private string ComputeVersion()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (SiteAsset asset in PublicationAssets)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(asset.Path + "\n"));
            using Stream stream = Resource(SitePrefix + asset.Path);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            digest.AppendData(copy.ToArray());
        }

        return "sha256:" + Convert.ToHexStringLower(digest.GetHashAndReset())[..16];
    }
}
