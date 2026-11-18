using System.Collections.Immutable;
using MySync.Domain.Manifests;

namespace MySync.Domain.Publishing;

public enum PublicationStatus
{
    /// <summary>Recorded before anything is uploaded; becomes Published or Failed.</summary>
    Pending,

    /// <summary>Its <c>index.html</c> is up: the link works.</summary>
    Published,

    /// <summary>Publishing stopped before the commit point; whatever was uploaded can be removed with <c>unpublish</c>.</summary>
    Failed,
}

/// <summary>One record of <c>shares/&lt;shareId&gt;/publications.json</c> (private account).</summary>
/// <param name="PublishAccount">The publishing storage account, so <c>unpublish</c> refuses to delete from another one.</param>
/// <param name="Url">The public URL (<c>&lt;base&gt;/p/&lt;publishId&gt;/</c>).</param>
public sealed record Publication(
    PublishId Id,
    PublicationStatus Status,
    string Title,
    Revision Revision,
    int Files,
    long Bytes,
    DateTimeOffset CreatedUtc,
    string CreatedBy,
    string PublishAccount,
    string Url,
    string SiteAssetsVersion);

/// <summary>
/// The share's publications, in creation order (the private <c>publications.json</c>). Changed
/// only by read-modify-write guarded by the blob's ETag, like the manifest.
/// </summary>
public sealed record PublicationRegistry(ImmutableList<Publication> Publications)
{
    public const int CurrentFormatVersion = 1;

    public static PublicationRegistry Empty { get; } = new([]);

    public Publication? Find(PublishId id) => Publications.Find(publication => publication.Id == id);

    /// <summary>Adds the publication, or replaces the one with its ID.</summary>
    public PublicationRegistry With(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        int at = Publications.FindIndex(existing => existing.Id == publication.Id);
        return new PublicationRegistry(at < 0 ? Publications.Add(publication) : Publications.SetItem(at, publication));
    }

    public PublicationRegistry Without(PublishId id) =>
        new(Publications.RemoveAll(publication => publication.Id == id));
}
