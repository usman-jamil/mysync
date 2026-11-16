using System.Collections.Concurrent;
using System.Globalization;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Paths;

namespace MySync.Testing;

/// <summary>
/// Writes every progress call down as one line, so a test pins the whole sequence
/// (CLAUDE.md §14). Thread-safe; with parallel transfers, pin order only with parallelism 1.
/// </summary>
public sealed class RecordingSyncProgressReporter : ISyncProgressReporter
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void PhaseStarted(SyncPhase phase) => _lines.Enqueue($"phase {phase}");

    public void TransfersPlanned(TransferDirection direction, int files, long bytes) =>
        _lines.Enqueue(string.Create(CultureInfo.InvariantCulture, $"planned {direction} {files} files {bytes} bytes"));

    public void BytesTransferred(TransferDirection direction, long bytes) =>
        _lines.Enqueue(string.Create(CultureInfo.InvariantCulture, $"bytes {direction} {bytes}"));

    public void FileTransferred(TransferDirection direction, SharePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _lines.Enqueue($"file {direction} {path.Value}");
    }
}
