using System.Text.Json.Serialization;
using MySync.Infrastructure.Serialization;

namespace MySync.Infrastructure.Publishing;

// site-data.js: the shape of apps/mysync-site/contract/site-data.schema.json. Property order is
// declaration order, so the output is deterministic.
internal sealed record SiteDataJson(
    int SchemaVersion,
    SiteDataPublicationJson Publication,
    SiteDataTotalsJson Totals,
    SiteDataDownloadsJson Downloads,
    IReadOnlyList<SiteDataEntryJson> Entries);

internal sealed record SiteDataPublicationJson(string Id, string Title, DateTimeOffset PublishedUtc, long Revision, string Generator);

internal sealed record SiteDataTotalsJson(long Files, long Bytes);

internal sealed record SiteDataDownloadsJson(string Sha256sums, string Shell, string Powershell);

// One shape for both kinds; a directory leaves the file fields out (null is never written).
internal sealed record SiteDataEntryJson(
    string Kind,
    string Path,
    long? Size = null,
    string? Sha256 = null,
    DateTimeOffset? MtimeUtc = null,
    string? Disposition = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(SiteDataJson))]
internal sealed partial class SiteDataJsonContext : JsonSerializerContext;

// publications.json (private account).
internal sealed record PublicationRegistryJson(int FormatVersion, IReadOnlyList<PublicationJson> Publications);

internal sealed record PublicationJson(
    string Id,
    string Status,
    string Title,
    long Revision,
    int Files,
    long Bytes,
    DateTimeOffset CreatedUtc,
    string CreatedBy,
    string PublishAccount,
    string Url,
    string SiteAssetsVersion);

internal sealed record RegistryFormatProbeJson(int FormatVersion);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(PublicationRegistryJson))]
[JsonSerializable(typeof(RegistryFormatProbeJson))]
internal sealed partial class PublicationRegistryJsonContext : JsonSerializerContext;
