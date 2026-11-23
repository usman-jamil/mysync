using System.Text.Json;
using System.Text.Json.Serialization;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Sync.Push;

namespace MySync.Infrastructure.Serialization;

/// <summary>
/// <c>_mysync-push.json</c> (CLAUDE.md §10b): source-generated, indented, properties in a fixed
/// order and times to the second, so the same push always gives the same bytes.
/// </summary>
internal sealed class PushRecordSerializer : IPushRecordSerializer
{
    public byte[] Serialize(PushRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        PushRecordJson json = new(
            PushRecord.FormatVersion,
            record.Host,
            record.User,
            record.OperatingSystem,
            record.MysyncVersion,
            ToSecond(record.StartedUtc),
            ToSecond(record.FinishedUtc),
            [.. record.Sources],
            record.Target,
            record.Files,
            record.Bytes,
            [.. record.Skipped.Select(skip => new PushRecordSkipJson(skip.Path, skip.Cause, skip.Detail))],
            record.Filters is { } filters
                ? new PushFiltersJson(ToSecond(filters.UpdatedSinceUtc), filters.UpdatedSinceSpec, [.. filters.Include], [.. filters.Exclude], filters.MaxSizeBytes)
                : null,
            record.Filters is not null && record.FilteredOut is { } counts
                ? new PushFilteredOutJson(counts.Total, counts.NotChanged, counts.NotIncluded, counts.Excluded, counts.TooLarge)
                : null);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(json, PushRecordJsonContext.Default.PushRecordJson);
        return [.. bytes, (byte)'\n'];
    }

    private static DateTimeOffset? ToSecond(DateTimeOffset? time) => time is { } value ? ToSecond(value) : null;

    private static DateTimeOffset ToSecond(DateTimeOffset time)
    {
        DateTimeOffset utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}

internal sealed record PushRecordJson(
    int FormatVersion,
    string Host,
    string User,
    string Os,
    string MysyncVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<string> Sources,
    string Target,
    int Files,
    long Bytes,
    IReadOnlyList<PushRecordSkipJson> Skipped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PushFiltersJson? Filters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PushFilteredOutJson? FilteredOut);

internal sealed record PushRecordSkipJson(string Path, string Cause, string Detail);

/// <summary>Only when filters were given (left out otherwise, so an unfiltered record is unchanged): this folder holds part of the source.</summary>
internal sealed record PushFiltersJson(DateTimeOffset? UpdatedSince, string? UpdatedSinceSpec, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, long? MaxSizeBytes);

internal sealed record PushFilteredOutJson(int Total, int NotChanged, int NotIncluded, int Excluded, int TooLarge);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(PushRecordJson))]
internal sealed partial class PushRecordJsonContext : JsonSerializerContext;
