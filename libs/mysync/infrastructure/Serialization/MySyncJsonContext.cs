using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySync.Infrastructure.Serialization;

/// <summary>
/// Source-generated JSON for the manifest and the state file (CLAUDE.md §12: no reflection
/// serialization). camelCase, indented for easy debugging, timestamps as UTC with a <c>Z</c>.
/// </summary>
/// <remarks>
/// Reading is strict where it guards data: a required property that is missing or <c>null</c>,
/// or a property given twice (a crafted manifest could otherwise smuggle in a second value), is
/// an error. Unknown properties are skipped, so a later minor addition doesn't break old readers.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(ManifestJson))]
[JsonSerializable(typeof(ShareStateJson))]
[JsonSerializable(typeof(FormatProbeJson))]
internal sealed partial class MySyncJsonContext : JsonSerializerContext;

/// <summary><c>2026-11-15T18:31:02Z</c>, with up to seven fraction digits when there are any, so mtimes round-trip exactly.</summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value)
            ? value
            : throw new JsonException($"'{text}' is not a timestamp.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
