using System.Text.Json;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Infrastructure.Serialization;

/// <summary>
/// The one manifest JSON format (CLAUDE.md §6). Every provider uses it, so manifests are
/// byte-identical wherever they're written: entries in ordinal path order, LF line endings, UTF-8
/// without a BOM, timestamps in UTC.
/// </summary>
/// <remarks>
/// A manifest comes from shared storage, so reading trusts nothing: the format version is checked
/// first (a newer manifest is "upgrade mysync", even if its shape changed), then every path, hash
/// and entry goes through the Domain.
/// </remarks>
internal sealed class ManifestSerializer : IManifestSerializer
{
    public byte[] Serialize(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ManifestJson json = new(
            Manifest.CurrentFormatVersion,
            manifest.ShareId.ToString(),
            manifest.Revision.Value,
            manifest.CreatedUtc,
            manifest.UpdatedUtc,
            manifest.UpdatedBy,
            EntryMapping.ToJson(manifest.Entries));

        return JsonSerializer.SerializeToUtf8Bytes(json, MySyncJsonContext.Default.ManifestJson);
    }

    public Result<Manifest> Deserialize(ReadOnlySpan<byte> json)
    {
        ManifestJson? parsed;
        try
        {
            FormatProbeJson? probe = JsonSerializer.Deserialize(json, MySyncJsonContext.Default.FormatProbeJson);
            if (probe is not null && probe.FormatVersion > Manifest.CurrentFormatVersion)
            {
                return Result.Failure<Manifest>(ShareErrors.UnsupportedFormat(probe.FormatVersion));
            }

            parsed = JsonSerializer.Deserialize(json, MySyncJsonContext.Default.ManifestJson);
        }
        catch (JsonException exception)
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest($"not valid manifest JSON ({exception.Message})"));
        }

        if (parsed is null)
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest("the manifest is empty"));
        }

        if (!ShareId.TryParse(parsed.ShareId, out ShareId? shareId))
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest("shareId is not a UUID"));
        }

        if (!Revision.TryCreate(parsed.Revision, out Revision? revision))
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest($"revision {parsed.Revision} is not valid"));
        }

        Result<List<KeyValuePair<Domain.Paths.SharePath, ManifestEntry>>> entries =
            EntryMapping.ToDomain(parsed.Entries, ShareErrors.InvalidManifest);
        if (entries.IsFailure)
        {
            return Result.Failure<Manifest>(entries.Error);
        }

        return Manifest.Restore(parsed.FormatVersion, shareId, revision, parsed.CreatedUtc, parsed.UpdatedUtc, parsed.UpdatedBy, entries.Value);
    }
}
