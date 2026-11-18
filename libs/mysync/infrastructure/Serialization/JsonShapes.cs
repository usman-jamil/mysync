using System.Text.Json.Serialization;

namespace MySync.Infrastructure.Serialization;

// The on-disk and in-blob JSON shapes (CLAUDE.md §6, §7). Plain data: every rule lives in the
// Domain, and EntryJson.ToDomain / the serializers validate through it. Property order here is
// the order in the file.

/// <summary><c>manifest.json</c> and <c>manifests/&lt;revision&gt;.json</c>.</summary>
internal sealed record ManifestJson(
    int FormatVersion,
    string ShareId,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string UpdatedBy,
    Dictionary<string, EntryJson> Entries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeletionJson? Deleted = null);

/// <summary>Only in a deleted share's manifest (format 2): who deleted it, and when.</summary>
internal sealed record DeletionJson(DateTimeOffset DeletedUtc, string DeletedBy);

/// <summary>A manifest or base entry: <c>{ "type": "dir" }</c> or a file with all of its fields.</summary>
internal sealed record EntryJson(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? MtimeUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Exec = null);

/// <summary><c>.mysynctool</c>.</summary>
internal sealed record ShareStateJson(
    int FormatVersion,
    string Provider,
    string AccountName,
    string Container,
    string ShareId,
    long BaseRevision,
    Dictionary<string, EntryJson> Base,
    Dictionary<string, HashCacheJson> HashCache,
    List<ConflictJson> Conflicts);

internal sealed record HashCacheJson(long Size, long MtimeTicksUtc, string Sha256);

/// <summary>
/// An open conflict. Each side is a full entry, or <c>null</c> when absent on that side; the
/// remote's revision and author are those of the HEAD the conflict was found against.
/// </summary>
internal sealed record ConflictJson(
    string Path,
    string Kind,
    EntryJson? Base,
    EntryJson? Local,
    EntryJson? Remote,
    long RemoteRevision,
    string RemoteUpdatedBy,
    DateTimeOffset FirstDetectedUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>Just enough to read the format version before trusting the rest of the shape.</summary>
internal sealed record FormatProbeJson(int FormatVersion);
