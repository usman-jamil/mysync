using System.Globalization;
using System.Text;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Sync.Push;

namespace MySync.Testing;

/// <summary>
/// A stand-in for Infrastructure's <c>PushRecordSerializer</c> where it isn't referenced: one
/// <c>key=value</c> line per field, and every record it was given in <see cref="Records"/>.
/// </summary>
public sealed class TestPushRecordSerializer : IPushRecordSerializer
{
    private readonly List<PushRecord> _records = [];

    /// <summary>Pads each record to at least this many bytes (comment lines), so compression policy doesn't keep it raw for being small.</summary>
    public int PadTo { get; set; }

    public IReadOnlyList<PushRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public byte[] Serialize(PushRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_records)
        {
            _records.Add(record);
        }

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"host={record.Host}\nuser={record.User}\nos={record.OperatingSystem}\nversion={record.MysyncVersion}\n");
        text.Append(CultureInfo.InvariantCulture, $"started={record.StartedUtc:O}\nfinished={record.FinishedUtc:O}\ntarget={record.Target}\n");
        text.Append(CultureInfo.InvariantCulture, $"sources={string.Join('|', record.Sources)}\nfiles={record.Files}\nbytes={record.Bytes}\n");
        foreach (PushRecordSkip skip in record.Skipped)
        {
            text.Append(CultureInfo.InvariantCulture, $"skipped={skip.Path}:{skip.Cause}\n");
        }

        while (text.Length < PadTo)
        {
            text.Append("# padding to make the record compressible\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
