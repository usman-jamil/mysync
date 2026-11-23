using System.Collections.Concurrent;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace MySync.Cli.Hosting;

/// <summary>
/// The log's way out: normally stderr (<paramref name="console"/>); while <c>mysync tui</c> has the
/// screen, nothing may reach stdout or stderr, so events are held in memory instead, and the
/// warnings and errors among them are handed to the interface's activity list (CLAUDE.md §10d).
/// </summary>
internal sealed class SwitchableLogSink(ILogEventSink console) : ILogEventSink
{
    private const int MaxHeld = 1000;

    private ConcurrentQueue<string>? _held;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ConcurrentQueue<string>? held = Volatile.Read(ref _held);
        if (held is null)
        {
            console.Emit(logEvent);
            return;
        }

        if (logEvent.Level >= LogEventLevel.Warning && held.Count < MaxHeld)
        {
            held.Enqueue(logEvent.RenderMessage(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Holds events in memory until the returned handle is disposed.</summary>
    public Capture Hold()
    {
        ConcurrentQueue<string> held = new();
        Volatile.Write(ref _held, held);
        return new Capture(this, held);
    }

    internal sealed class Capture(SwitchableLogSink sink, ConcurrentQueue<string> held) : IDisposable
    {
        /// <summary>The warnings and errors logged since the last call.</summary>
        public IReadOnlyList<string> Drain()
        {
            List<string> lines = [];
            while (held.TryDequeue(out string? line))
            {
                lines.Add(line);
            }

            return lines;
        }

        public void Dispose() => Interlocked.CompareExchange(ref sink._held, null, held);
    }
}
