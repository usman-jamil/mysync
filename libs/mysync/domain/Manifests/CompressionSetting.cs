namespace MySync.Domain.Manifests;

/// <summary>
/// A share's compression setting (CLAUDE.md §6), recorded in its manifest so every machine applies
/// the same one. It governs only objects uploaded from then on; stored objects are never rewritten.
/// </summary>
public enum CompressionSetting
{
    None = 0,
    Gzip = 1,
}

public static class CompressionSettings
{
    /// <summary>The manifest and <c>--json</c> text form: <c>none</c> or <c>gzip</c>.</summary>
    public static string ToCode(this CompressionSetting setting) => setting == CompressionSetting.Gzip ? "gzip" : "none";

    public static bool TryParse(string? code, out CompressionSetting setting)
    {
        setting = code switch
        {
            "gzip" => CompressionSetting.Gzip,
            _ => CompressionSetting.None,
        };
        return code is "gzip" or "none";
    }
}
