using MySync.Domain.Paths;

namespace MySync.Application.Abstractions.Progress;

public enum SyncPhase
{
    Scanning = 0,
    Uploading = 1,
    Committing = 2,
    Downloading = 3,
}

public enum TransferDirection
{
    Upload = 0,
    Download = 1,
}

/// <summary>
/// Progress of one sync, clone or resolve (CLAUDE.md §4). Carried on the command, never a DI
/// service. Bytes are counted where they leave or reach the file. Transfers run in parallel, so
/// implementations must be thread-safe.
/// </summary>
public interface ISyncProgressReporter
{
    void PhaseStarted(SyncPhase phase);

    void TransfersPlanned(TransferDirection direction, int files, long bytes);

    void BytesTransferred(TransferDirection direction, long bytes);

    void FileTransferred(TransferDirection direction, SharePath path);
}

/// <summary>Reports nothing: the default when a command supplies no display.</summary>
public sealed class NullSyncProgressReporter : ISyncProgressReporter
{
    private NullSyncProgressReporter()
    {
    }

    public static NullSyncProgressReporter Instance { get; } = new();

    public void PhaseStarted(SyncPhase phase)
    {
    }

    public void TransfersPlanned(TransferDirection direction, int files, long bytes)
    {
    }

    public void BytesTransferred(TransferDirection direction, long bytes)
    {
    }

    public void FileTransferred(TransferDirection direction, SharePath path)
    {
    }
}
