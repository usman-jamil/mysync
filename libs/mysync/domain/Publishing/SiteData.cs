using System.Collections.Immutable;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Publishing;

/// <summary>
/// What <c>site-data.js</c> assigns to <c>window.__MYSYNC_SITE__</c>: the only input of the
/// publication page. Its JSON shape is <c>apps/mysync-site/contract/site-data.schema.json</c>.
/// </summary>
public sealed record SiteData(
    SiteDataPublication Publication,
    long TotalFiles,
    long TotalBytes,
    ImmutableArray<SiteDataEntry> Entries)
{
    public const int SchemaVersion = 1;
}

public sealed record SiteDataPublication(
    PublishId Id,
    string Title,
    DateTimeOffset PublishedUtc,
    Revision Revision,
    string Generator);

public abstract record SiteDataEntry(SharePath Path);

public sealed record SiteDataFile(SharePath Path, long Size, ContentHash Sha256, DateTimeOffset MtimeUtc, Disposition Disposition) : SiteDataEntry(Path);

/// <summary>Only empty directories; non-empty ones are implied by their files.</summary>
public sealed record SiteDataDirectory(SharePath Path) : SiteDataEntry(Path);

/// <summary>A manifest entry left out of a publication, and why.</summary>
public sealed record UnpublishablePath(SharePath Path, string Reason);

/// <summary>Builds a publication from a manifest. Pure: the I/O is the handler's.</summary>
public static class SiteDataBuilder
{
    /// <summary>
    /// The site data for <paramref name="manifest"/>, entries in ordinal path order, plus the paths
    /// that can't be published (<see cref="PublicationLayout.CannotPublish"/>), which are left out.
    /// </summary>
    public static (SiteData Data, ImmutableArray<UnpublishablePath> Skipped) Build(Manifest manifest, SiteDataPublication publication)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publication);

        var layout = PublicationLayout.For(publication.Id);
        ImmutableArray<SiteDataEntry>.Builder entries = ImmutableArray.CreateBuilder<SiteDataEntry>();
        ImmutableArray<UnpublishablePath>.Builder skipped = ImmutableArray.CreateBuilder<UnpublishablePath>();
        long files = 0;
        long bytes = 0;

        foreach ((SharePath path, ManifestEntry entry) in manifest.Entries.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
        {
            if (layout.CannotPublish(path) is { } reason)
            {
                skipped.Add(new UnpublishablePath(path, reason));
                continue;
            }

            switch (entry)
            {
                case FileEntry file:
                    entries.Add(new SiteDataFile(path, file.Size, file.Sha256, file.MtimeUtc, DispositionPolicy.For(path.Value)));
                    files++;
                    bytes += file.Size;
                    break;
                case DirectoryEntry:
                    entries.Add(new SiteDataDirectory(path));
                    break;
            }
        }

        return (new SiteData(publication, files, bytes, entries.ToImmutable()), skipped.ToImmutable());
    }
}
