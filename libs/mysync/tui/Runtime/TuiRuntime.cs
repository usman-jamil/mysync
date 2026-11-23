using System.Collections.Immutable;
using System.Threading.Channels;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Session;
using MySync.Tui.Effects;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using MySync.Tui.Terminal;
using MySync.Tui.Update;
using MySync.Tui.Views;
using SharedKernel;

namespace MySync.Tui.Runtime;

/// <summary>
/// The loop (CLAUDE.md §10d): read keys, session events, ticks and resizes as messages, run
/// <see cref="TuiUpdate.Update"/>, carry out its effects against the <see cref="IFolderSession"/>, and
/// draw <see cref="Screens.Render"/> at most about 10 times a second during a job. The terminal is
/// always given back, whatever ends the loop: a quit, Ctrl+C, a stop request, or an exception.
/// </summary>
/// <param name="drainLog">Warnings and errors the log collected while the screen is open: shown in the activity list.</param>
/// <param name="render">The views (tests replace them to simulate a failing view).</param>
public sealed class TuiRuntime(
    ITerminal terminal,
    IFolderSession session,
    TuiOptions options,
    TimeProvider time,
    Func<IReadOnlyList<string>>? drainLog = null,
    Func<TuiState, Frame>? render = null)
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<Msg> _results = Channel.CreateUnbounded<Msg>();
    private readonly Func<TuiState, Frame> _render = render ?? Screens.Render;
    private TuiState? _state;

    /// <summary>The last state (for tests).</summary>
    public TuiState? State => _state;

    /// <returns>The exit code: 0 for a quit, 130 when Ctrl+C or a stop request ended it during a job.</returns>
    public async Task<int> RunAsync(CancellationToken stop)
    {
        terminal.Enter();
        try
        {
            return await LoopAsync(stop);
        }
        finally
        {
            terminal.Leave();
        }
    }

    private async Task<int> LoopAsync(CancellationToken stop)
    {
        (int width, int height) = terminal.Size;
        _state = TuiUpdate.Initial(options with { Colors = terminal.Colors }, width, height, time.GetUtcNow());
        int? exit = Run([new StartRescan()]);

        using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<KeyInput?> key = terminal.ReadKeyAsync(reading.Token);
        Task<bool> sessionEvent = session.WaitForEventAsync(reading.Token);
        Task<bool> result = _results.Reader.WaitToReadAsync(reading.Token).AsTask();
        long lastDraw = 0;
        DateTimeOffset lastTick = time.GetUtcNow();
        bool dirty = true;

        try
        {
            while (exit is null)
            {
                if (stop.IsCancellationRequested)
                {
                    exit = _state.IsBusy ? 130 : 0;
                    break;
                }

                dirty |= Drain(ref exit);
                (int w, int h) = terminal.Size;
                if (w != _state.Width || h != _state.Height)
                {
                    exit ??= Handle(new Resized(w, h));
                    dirty = true;
                }

                DateTimeOffset now = time.GetUtcNow();
                if (now - lastTick >= TickInterval)
                {
                    exit ??= Handle(new Tick(now));
                    lastTick = now;
                    dirty = true;
                }

                if (dirty && (!_state.IsBusy || time.GetElapsedTime(lastDraw) >= FrameInterval))
                {
                    terminal.Draw(_render(_state));
                    lastDraw = time.GetTimestamp();
                    dirty = false;
                }

                if (exit is not null)
                {
                    break;
                }

                var delay = Task.Delay(dirty ? FrameInterval : TimeSpan.FromMilliseconds(250), time, reading.Token);
                await Task.WhenAny(key, sessionEvent, result, delay);

                if (key.IsCompleted)
                {
                    KeyInput? pressed = await key;
                    if (pressed is null)
                    {
                        exit = 0; // input ended
                        break;
                    }

                    exit = Handle(new KeyPressed(pressed.Value));
                    dirty = true;
                    key = terminal.ReadKeyAsync(reading.Token);
                }

                if (sessionEvent.IsCompleted)
                {
                    sessionEvent = session.WaitForEventAsync(reading.Token);
                }

                if (result.IsCompleted)
                {
                    result = _results.Reader.WaitToReadAsync(reading.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            exit = _state.IsBusy ? 130 : 0;
        }
        finally
        {
            await reading.CancelAsync();
        }

        if (session.Running is not null)
        {
            session.Cancel();
            await session.WhenIdleAsync();
        }

        return exit ?? 0;
    }

    /// <summary>Everything waiting: session events, finished diffs, log lines.</summary>
    private bool Drain(ref int? exit)
    {
        bool any = false;
        while (session.TryReadEvent(out SessionEvent? sessionEvent))
        {
            exit ??= Handle(new SessionMsg(sessionEvent!));
            any = true;
        }

        while (_results.Reader.TryRead(out Msg? message))
        {
            exit ??= Handle(message);
            any = true;
        }

        foreach (string line in drainLog?.Invoke() ?? [])
        {
            exit ??= Handle(new LogLine(line));
            any = true;
        }

        return any;
    }

    private int? Handle(Msg message)
    {
        (TuiState next, ImmutableArray<Effect> effects) = TuiUpdate.Update(_state!, message);
        _state = next;
        return Run(effects);
    }

    private int? Run(ImmutableArray<Effect> effects)
    {
        int? exit = null;
        foreach (Effect effect in effects)
        {
            switch (effect)
            {
                case StartRescan:
                    Refused(session.Rescan());
                    break;
                case StartSync sync:
                    Refused(session.Sync(sync.AllowMassDelete));
                    break;
                case StartResolve resolve:
                    Refused(session.Resolve([.. resolve.Paths], resolve.All, resolve.Keep));
                    break;
                case StartPush push:
                    Refused(session.Push(push.Path, push.Share, push.Create));
                    break;
                case LoadDiff diff:
                    _ = LoadDiffAsync(diff.Path);
                    break;
                case CancelJob:
                    session.Cancel();
                    break;
                case Quit quit:
                    exit = quit.ExitCode;
                    break;
                default:
                    break;
            }
        }

        return exit;
    }

    private void Refused(Result started)
    {
        if (started.IsFailure)
        {
            _results.Writer.TryWrite(new Refused(started.Error));
        }
    }

    private async Task LoadDiffAsync(string path)
    {
        Result<DiffResponse> diff = await session.DiffAsync(path, CancellationToken.None);
        _results.Writer.TryWrite(diff.IsSuccess ? new DiffLoaded(path, diff.Value, null) : new DiffLoaded(path, null, diff.Error));
    }
}
