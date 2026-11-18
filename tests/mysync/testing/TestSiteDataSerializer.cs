using System.Text;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Publishing;

namespace MySync.Testing;

/// <summary>
/// A stand-in for Infrastructure's <c>SiteDataSerializer</c> where it isn't referenced: enough of
/// <c>site-data.js</c> to find the publication ID and every path in it.
/// </summary>
public sealed class TestSiteDataSerializer : ISiteDataSerializer
{
    public byte[] Serialize(SiteData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        StringBuilder text = new($"window.__MYSYNC_SITE__ = {{\"id\":\"{data.Publication.Id}\",\"title\":\"{data.Publication.Title}\",\"paths\":[");
        text.AppendJoin(',', data.Entries.Select(entry => $"\"{entry.Path.Value}\""));
        return Encoding.UTF8.GetBytes(text.Append("]};\n").ToString());
    }
}
