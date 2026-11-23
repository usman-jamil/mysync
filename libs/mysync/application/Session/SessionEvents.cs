using System.Threading.Channels;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Application.Session;

/// <summary>The kinds of job a folder session runs, one at a time.</summary>
public enum JobKind
{
    Rescan = 0,
    Sync = 1,
    Resolve = 2,
    Push = 3,
}

/// <summary>A job of the session.</summary>
/// <param name="Name">How messages name it: <c>rescan</c>, <c>sync</c>, <c>resolve</c>, <c>push</c>.</param>
/// <param name="PushPath">A push: the item pushed (relative to the folder).</param>
/// <param name="PushShare">A push: the share it goes to, as typed.</param>
public sealed record SessionJob(JobKind Kind, string Name, string? PushPath = null, string? PushShare = null)
{
    public static SessionJob Rescan { get; } = new(JobKind.Rescan, "rescan");

    public static SessionJob Sync { get; } = new(JobKind.Sync, "sync");

    public static SessionJob Resolve { get; } = new(JobKind.Resolve, "resolve");
}

/// <summary>What a finished job produced: the status for a rescan, the report for a sync, resolve or push.</summary>
public sealed record JobResult(FolderStatus? Status = null, SyncReport? Sync = null, PushReport? Push = null);

/// <summary>Something that happened in the session. A front end reads these in order.</summary>
public abstract record SessionEvent;

public sealed record JobStarted(SessionJob Job) : SessionEvent;

public sealed record JobFinished(SessionJob Job, JobResult Result) : SessionEvent;

public sealed record JobFailed(SessionJob Job, Error Error) : SessionEvent;

public sealed record JobCancelled(SessionJob Job) : SessionEvent;

public sealed record PhaseStarted(SyncPhase Phase) : SessionEvent;

public sealed record TransfersPlanned(TransferDirection Direction, int Files, long Bytes) : SessionEvent;

/// <summary>Bytes done so far in one direction (cumulative), and the recent speed. Coalesced: at most about 10 a second per direction.</summary>
public sealed record TransferProgress(TransferDirection Direction, long BytesDone, long BytesPerSecond) : SessionEvent;

/// <summary>A file's transfer began (display only).</summary>
public sealed record FileStarted(TransferDirection Direction, SharePath Path) : SessionEvent;

public sealed record FileFinished(TransferDirection Direction, SharePath Path) : SessionEvent;

/// <summary>
/// The session's events. Writing never blocks a job: the channel is unbounded, and display-only
/// events (<see cref="TransferProgress"/>, <see cref="FileStarted"/>) are dropped while a few of
/// them are still unread, so a stalled reader costs a job nothing and memory stays flat.
/// </summary>
internal sealed class SessionEventQueue
{
    private const int MaxPendingTransient = 4;

    private readonly Channel<SessionEvent> _channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private int _pendingTransient;

    public void Write(SessionEvent sessionEvent)
    {
        if (IsTransient(sessionEvent) && Interlocked.Increment(ref _pendingTransient) > MaxPendingTransient)
        {
            Interlocked.Decrement(ref _pendingTransient);
            return;
        }

        _channel.Writer.TryWrite(sessionEvent);
    }

    public bool TryRead(out SessionEvent? sessionEvent)
    {
        if (!_channel.Reader.TryRead(out sessionEvent))
        {
            return false;
        }

        if (IsTransient(sessionEvent))
        {
            Interlocked.Decrement(ref _pendingTransient);
        }

        return true;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);

    private static bool IsTransient(SessionEvent sessionEvent) => sessionEvent is TransferProgress or FileStarted;
}

/// <summary>
/// Turns the pipeline's progress calls into session events. Byte counts are added up per
/// direction and sent at most every <see cref="Interval"/>, with a speed from the time between
/// sends; a finished file always sends the count so far first.
/// </summary>
internal sealed class SessionProgress(SessionEventQueue events, TimeProvider time) : ISyncProgressReporter
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly Direction[] _directions = [new(), new()];

    public void PhaseStarted(SyncPhase phase) => events.Write(new PhaseStarted(phase));

    public void TransfersPlanned(TransferDirection direction, int files, long bytes)
    {
        lock (_gate)
        {
            Direction state = _directions[(int)direction];
            state.Done = 0;
            state.SentDone = 0;
            state.SentAt = time.GetTimestamp();
        }

        events.Write(new TransfersPlanned(direction, files, bytes));
    }

    public void BytesTransferred(TransferDirection direction, long bytes)
    {
        TransferProgress? due = null;
        lock (_gate)
        {
            Direction state = _directions[(int)direction];
            state.Done += bytes;
            if (time.GetElapsedTime(state.SentAt) >= Interval)
            {
                due = Take(direction, state);
            }
        }

        if (due is not null)
        {
            events.Write(due);
        }
    }

    public void FileStarted(TransferDirection direction, SharePath path) => events.Write(new FileStarted(direction, path));

    public void FileTransferred(TransferDirection direction, SharePath path)
    {
        TransferProgress progress;
        lock (_gate)
        {
            progress = Take(direction, _directions[(int)direction]);
        }

        events.Write(progress);
        events.Write(new FileFinished(direction, path));
    }

    private TransferProgress Take(TransferDirection direction, Direction state)
    {
        long now = time.GetTimestamp();
        TimeSpan elapsed = time.GetElapsedTime(state.SentAt, now);
        long rate = elapsed > TimeSpan.Zero ? (long)((state.Done - state.SentDone) / elapsed.TotalSeconds) : 0;
        state.SentAt = now;
        state.SentDone = state.Done;
        return new TransferProgress(direction, state.Done, rate);
    }

    private sealed class Direction
    {
        public long Done { get; set; }

        public long SentDone { get; set; }

        public long SentAt { get; set; }
    }
}
