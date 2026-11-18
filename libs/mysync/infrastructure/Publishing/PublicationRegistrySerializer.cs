using System.Collections.Immutable;
using System.Text.Json;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using SharedKernel;

namespace MySync.Infrastructure.Publishing;

/// <summary><c>shares/&lt;shareId&gt;/publications.json</c>. Read strictly: it decides what <c>unpublish</c> and <c>delete</c> remove.</summary>
internal sealed class PublicationRegistrySerializer : IPublicationRegistrySerializer
{
    public byte[] Serialize(PublicationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        PublicationRegistryJson json = new(
            PublicationRegistry.CurrentFormatVersion,
            [.. registry.Publications.Select(publication => new PublicationJson(
                publication.Id.Value,
                StatusName(publication.Status),
                publication.Title,
                publication.Revision.Value,
                publication.Files,
                publication.Bytes,
                publication.CreatedUtc,
                publication.CreatedBy,
                publication.PublishAccount,
                publication.Url,
                publication.SiteAssetsVersion))]);

        return JsonSerializer.SerializeToUtf8Bytes(json, PublicationRegistryJsonContext.Default.PublicationRegistryJson);
    }

    private static string StatusName(PublicationStatus status) => status switch
    {
        PublicationStatus.Pending => "pending",
        PublicationStatus.Published => "published",
        PublicationStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public Result<PublicationRegistry> Deserialize(ReadOnlySpan<byte> json)
    {
        PublicationRegistryJson? parsed;
        try
        {
            RegistryFormatProbeJson? probe = JsonSerializer.Deserialize(json, PublicationRegistryJsonContext.Default.RegistryFormatProbeJson);
            if (probe is not null && probe.FormatVersion > PublicationRegistry.CurrentFormatVersion)
            {
                return Result.Failure<PublicationRegistry>(PublishErrors.InvalidRegistry($"it was written by a newer mysync (format {probe.FormatVersion}); upgrade mysync."));
            }

            parsed = JsonSerializer.Deserialize(json, PublicationRegistryJsonContext.Default.PublicationRegistryJson);
        }
        catch (JsonException)
        {
            return Result.Failure<PublicationRegistry>(PublishErrors.InvalidRegistry("it isn't valid JSON."));
        }

        if (parsed is null)
        {
            return Result.Failure<PublicationRegistry>(PublishErrors.InvalidRegistry("it is empty."));
        }

        ImmutableList<Publication>.Builder publications = ImmutableList.CreateBuilder<Publication>();
        foreach (PublicationJson item in parsed.Publications)
        {
            if (!PublishId.TryParse(item.Id, out PublishId? id)
                || !Revision.TryCreate(item.Revision, out Revision? revision)
                || !Enum.TryParse(item.Status, ignoreCase: true, out PublicationStatus status)
                || !Enum.IsDefined(status))
            {
                return Result.Failure<PublicationRegistry>(PublishErrors.InvalidRegistry($"the record '{item.Id}' is malformed."));
            }

            publications.Add(new Publication(
                id, status, item.Title, revision, item.Files, item.Bytes, item.CreatedUtc, item.CreatedBy,
                item.PublishAccount, item.Url, item.SiteAssetsVersion));
        }

        return new PublicationRegistry(publications.ToImmutable());
    }
}
