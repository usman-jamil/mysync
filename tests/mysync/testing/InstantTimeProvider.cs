using System.Collections.Concurrent;

namespace MySync.Testing;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire at once, so retry back-offs don't slow tests
/// down, and which records every delay asked for, so tests can check them.
/// </summary>
public sealed class InstantTimeProvider : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _delays = new();

    public IReadOnlyList<TimeSpan> Delays => [.. _delays];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            _delays.Enqueue(dueTime);

            // Not inline: the caller may not have stored the timer yet.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
