using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;

namespace MySync.Domain.Storage;

/// <summary>
/// The one place blob names are composed (CLAUDE.md §6). Provider-agnostic: an S3 provider uses
/// the same keys. An instance per share keeps a later move to one container per share to a
/// change of <see cref="Prefix"/>.
/// </summary>
/// <example>
/// <code>
/// shares/&lt;shareId&gt;/manifest.json
/// shares/&lt;shareId&gt;/manifests/0000000017.json
/// shares/&lt;shareId&gt;/objects/9f/9f86d0…
/// </code>
/// </example>
public sealed class ShareLayout
{
    private const string ManifestHistoryExtension = ".json";
    private const int RevisionDigits = 10;

    private ShareLayout(ShareId shareId)
    {
        ShareId = shareId;
        Prefix = $"shares/{shareId}/";
        Manifest = Prefix + "manifest.json";
        ManifestHistoryPrefix = Prefix + "manifests/";
        ObjectsPrefix = Prefix + "objects/";
        Publications = Prefix + "publications.json";
    }

    public ShareId ShareId { get; }

    public string Prefix { get; }

    /// <summary>HEAD: the single commit point.</summary>
    public string Manifest { get; }

    public string ManifestHistoryPrefix { get; }

    public string ObjectsPrefix { get; }

    /// <summary>The share's publication records (<c>mysync publish</c>), ETag-guarded like HEAD.</summary>
    public string Publications { get; }

    public static ShareLayout For(ShareId shareId)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        return new ShareLayout(shareId);
    }

    /// <summary>Zero-padded to 10 digits, so names sort in revision order.</summary>
    public string ManifestHistory(Revision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ManifestHistoryPrefix
            + revision.Value.ToString("D" + RevisionDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            + ManifestHistoryExtension;
    }

    public string ObjectBlob(ContentHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return $"{ObjectsPrefix}{hash.FanOut}/{hash.Value}";
    }

    /// <summary>
    /// The hash of an object blob in this share. Only the canonical name is accepted (right
    /// share, right fan-out folder, lowercase), so a stray blob is never mistaken for an object.
    /// </summary>
    public bool TryParseObject(string blobName, [NotNullWhen(true)] out ContentHash? hash)
    {
        ArgumentNullException.ThrowIfNull(blobName);
        hash = null;

        if (!blobName.StartsWith(ObjectsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = blobName[(blobName.LastIndexOf('/') + 1)..];

        if (!ContentHash.TryParse(name, out ContentHash? parsed)
            || !string.Equals(ObjectBlob(parsed), blobName, StringComparison.Ordinal))
        {
            return false;
        }

        hash = parsed;
        return true;
    }

    /// <summary>The revision of a history manifest blob in this share; canonical names only.</summary>
    public bool TryParseManifestHistory(string blobName, [NotNullWhen(true)] out Revision? revision)
    {
        ArgumentNullException.ThrowIfNull(blobName);
        revision = null;

        if (!blobName.StartsWith(ManifestHistoryPrefix, StringComparison.Ordinal)
            || !blobName.EndsWith(ManifestHistoryExtension, StringComparison.Ordinal))
        {
            return false;
        }

        string digits = blobName[ManifestHistoryPrefix.Length..^ManifestHistoryExtension.Length];

        if (!digits.All(char.IsAsciiDigit)
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || !Revision.TryCreate(value, out Revision? parsed)
            || !string.Equals(ManifestHistory(parsed), blobName, StringComparison.Ordinal))
        {
            return false;
        }

        revision = parsed;
        return true;
    }
}
