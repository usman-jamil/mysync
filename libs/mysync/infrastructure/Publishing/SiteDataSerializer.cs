using System.Text;
using System.Text.Json;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Publishing;

namespace MySync.Infrastructure.Publishing;

/// <summary>
/// <c>site-data.js</c>: <c>window.__MYSYNC_SITE__ = {…};</c> with the JSON of
/// <c>apps/mysync-site/contract/site-data.schema.json</c>. Source-generated, compact, and with
/// the default escaping encoder, so a file name can never close the string or the script
/// (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, quotes and every non-ASCII character are <c>\uXXXX</c>).
/// </summary>
internal sealed class SiteDataSerializer : ISiteDataSerializer
{
    public const string Prefix = "window.__MYSYNC_SITE__ = ";

    public byte[] Serialize(SiteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        SiteDataJson json = new(
            SiteData.SchemaVersion,
            new SiteDataPublicationJson(
                data.Publication.Id.Value,
                data.Publication.Title,
                data.Publication.PublishedUtc,
                data.Publication.Revision.Value,
                data.Publication.Generator),
            new SiteDataTotalsJson(data.TotalFiles, data.TotalBytes),
            new SiteDataDownloadsJson(PublicationLayout.Sha256Sums, PublicationLayout.ShellScript, PublicationLayout.PowerShellScript),
            [.. data.Entries.Select(Entry)]);

        string text = Prefix + JsonSerializer.Serialize(json, SiteDataJsonContext.Default.SiteDataJson) + ";\n";
        return Encoding.UTF8.GetBytes(text);
    }

    private static SiteDataEntryJson Entry(SiteDataEntry entry) => entry switch
    {
        SiteDataFile file => new SiteDataEntryJson(
            "file", file.Path.Value, file.Size, file.Sha256.Value, file.MtimeUtc,
            file.Disposition == Disposition.Inline ? "inline" : "attachment"),
        _ => new SiteDataEntryJson("dir", entry.Path.Value),
    };
}
