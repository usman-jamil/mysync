using MySync.Application.Abstractions.Progress;
using MySync.Domain.Paths;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MySync.Cli.Rendering;

/// <summary>
/// Progress of one sync, clone or create (CLAUDE.md §4), on stderr. One instance per invocation,
/// created by the command for the length of one run, never a DI service.
/// </summary>
/// <remarks>
/// With a terminal that can redraw it draws upload and download bars. Without one (piped, CI,
/// <c>TERM=dumb</c>) it prints one line per phase and per planned transfer, so logs stay readable.
/// <c>--quiet</c> and <c>--json</c> show nothing. Transfers run in parallel, so every method is
/// thread-safe.
/// </remarks>
internal abstract class SyncProgressDisplay : ISyncProgressReporter
{
    public abstract void PhaseStarted(SyncPhase phase);

    public abstract void TransfersPlanned(TransferDirection direction, int files, long bytes);

    public abstract void BytesTransferred(TransferDirection direction, long bytes);

    public abstract void FileTransferred(TransferDirection direction, SharePath path);

    /// <summary>Runs <paramref name="run"/> with the right display for this terminal and these options.</summary>
    public static async Task<T> RunAsync<T>(GlobalOptions options, Func<ISyncProgressReporter, Task<T>> run)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(run);

        if (options.Json || options.Quiet)
        {
            return await run(NullSyncProgressReporter.Instance);
        }

        if (!ConsoleOutput.CanRedrawErrors)
        {
            return await run(new Lines());
        }

        T result = default!;
        await ConsoleOutput.Errors.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new TransfersOnly(new PercentageColumn()),
                new TransfersOnly(new DownloadedColumn()),
                new TransfersOnly(new TransferSpeedColumn()))
            .StartAsync(async context => result = await run(new Bars(context)));
        return result;
    }

    private static string Phase(SyncPhase phase) => phase switch
    {
        SyncPhase.Scanning => "Scanning the folder…",
        SyncPhase.Uploading => "Uploading…",
        SyncPhase.Committing => "Committing…",
        SyncPhase.Downloading => "Downloading…",
        _ => phase.ToString(),
    };

    private const string PhaseKey = "phase";

    private static string Arrow(TransferDirection direction) => direction == TransferDirection.Upload ? "↑" : "↓";

    /// <summary>A redrawable terminal: one bar per direction.</summary>
    private sealed class Bars(ProgressContext context) : SyncProgressDisplay
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<TransferDirection, ProgressTask> _bars = [];
        private ProgressTask? _phase;

        public override void PhaseStarted(SyncPhase phase)
        {
            lock (_gate)
            {
                if (_phase is not null)
                {
                    _phase.Value = _phase.MaxValue; // done: a full bar, not a stalled one
                    _phase.IsIndeterminate = false;
                    _phase.StopTask();
                }

                _phase = null;
                if (phase is SyncPhase.Scanning or SyncPhase.Committing)
                {
                    _phase = context.AddTask(Markup.Escape(Phase(phase)), maxValue: 1).IsIndeterminate();
                    _phase.State.Update<bool>(PhaseKey, _ => true);
                }
            }
        }

        public override void TransfersPlanned(TransferDirection direction, int files, long bytes)
        {
            if (files == 0)
            {
                return;
            }

            lock (_gate)
            {
                _bars[direction] = context.AddTask(
                    Markup.Escape($"{Arrow(direction)} {files} {(files == 1 ? "file" : "files")}"),
                    maxValue: Math.Max(bytes, 1));
            }
        }

        public override void BytesTransferred(TransferDirection direction, long bytes)
        {
            lock (_gate)
            {
                if (_bars.TryGetValue(direction, out ProgressTask? bar))
                {
                    bar.Increment(bytes);
                }
            }
        }

        public override void FileTransferred(TransferDirection direction, SharePath path)
        {
            // The bar counts bytes; a per-file line would scroll the bars away.
        }
    }

    /// <summary>Byte counts and speed mean nothing for "Scanning…" or "Committing…": blank there.</summary>
    private sealed class TransfersOnly(ProgressColumn inner) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
            task.State.Get<bool>(PhaseKey) ? Text.Empty : inner.Render(options, task, deltaTime);
    }

    /// <summary>No redrawing: a line per phase and per planned transfer, nothing per byte.</summary>
    private sealed class Lines : SyncProgressDisplay
    {
        public override void PhaseStarted(SyncPhase phase)
        {
            if (phase is SyncPhase.Scanning or SyncPhase.Committing)
            {
                ConsoleOutput.Info(Phase(phase));
            }
        }

        public override void TransfersPlanned(TransferDirection direction, int files, long bytes)
        {
            if (files > 0)
            {
                ConsoleOutput.Info($"{Arrow(direction)} {(direction == TransferDirection.Upload ? "Uploading" : "Downloading")} {files} {(files == 1 ? "file" : "files")} ({ConsoleOutput.Bytes(bytes)})");
            }
        }

        public override void BytesTransferred(TransferDirection direction, long bytes)
        {
            // Too frequent for a line each.
        }

        public override void FileTransferred(TransferDirection direction, SharePath path)
        {
            // As above; the summary lists what changed.
        }
    }
}
