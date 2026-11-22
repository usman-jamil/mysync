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
            [.. record.Skipped.Select(skip => new PushRecordSkipJson(skip.Path, skip.Cause, skip.Detail))]);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(json, PushRecordJsonContext.Default.PushRecordJson);
        return [.. bytes, (byte)'\n'];
    }

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
    IReadOnlyList<PushRecordSkipJson> Skipped);

internal sealed record PushRecordSkipJson(string Path, string Cause, string Detail);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(PushRecordJson))]
internal sealed partial class PushRecordJsonContext : JsonSerializerContext;
