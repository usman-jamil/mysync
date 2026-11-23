using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.Terminal;

namespace MySync.Testing;

/// <summary>
/// A terminal for driving <c>mysync tui</c> in tests: scripted steps (keys, waits for something to
/// appear on screen, actions in between), every frame recorded, and whether the screen was given
/// back. When the script runs out, input ends (the interface quits).
/// </summary>
public sealed class ScriptedTerminal(int width = 100, int height = 30, ColorDepth colors = ColorDepth.TrueColor) : ITerminal
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly Queue<Func<CancellationToken, Task<KeyInput?>>> _steps = new();
    private readonly Lock _gate = new();
    private readonly List<Frame> _frames = [];
    private TaskCompletionSource _drawn = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public (int Width, int Height) Size { get; set; } = (width, height);

    public ColorDepth Colors => colors;

    public int Enters { get; private set; }

    public int Leaves { get; private set; }

    public bool IsShowing => Enters > Leaves;

    public IReadOnlyList<Frame> Frames
    {
        get
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>The text of the last frame drawn.</summary>
    public string Screen
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count > 0 ? _frames[^1].ToText() : string.Empty;
            }
        }
    }

    public ScriptedTerminal Press(params KeyInput[] keys)
    {
        foreach (KeyInput key in keys)
        {
            _steps.Enqueue(_ => Task.FromResult<KeyInput?>(key));
        }

        return this;
    }

    public ScriptedTerminal Press(char key) => Press(KeyInput.Of(key));

    public ScriptedTerminal Press(KeyCode key, bool shift = false) => Press(KeyInput.Of(key, shift));

    public ScriptedTerminal Type(string text) => Press([.. text.Select(KeyInput.Of)]);

    /// <summary>Waits until a drawn frame contains <paramref name="text"/> (fails after 20 s, showing the screen).</summary>
    public ScriptedTerminal WaitFor(string text) => WaitUntil(screen => screen.Contains(text, StringComparison.Ordinal), $"'{text}' on screen");

    public ScriptedTerminal WaitUntil(Func<string, bool> condition, string description)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _steps.Enqueue(async cancellationToken =>
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(WaitLimit);
            while (!condition(Screen))
            {
                Task drawn;
                lock (_gate)
                {
                    drawn = _drawn.Task;
                }

                try
                {
                    await drawn.WaitAsync(limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Waited for {description}; the screen was:\n{Screen}");
                }
            }

            return null;
        });
        return this;
    }

    /// <summary>Runs <paramref name="action"/> at this point of the script (another machine syncs, …).</summary>
    public ScriptedTerminal ThenAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _steps.Enqueue(async _ =>
        {
            await action();
            return null;
        });
        return this;
    }

    /// <summary>Runs <paramref name="action"/> at this point of the script (change a file, resize, …).</summary>
    public ScriptedTerminal Then(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _steps.Enqueue(_ =>
        {
            action();
            return Task.FromResult<KeyInput?>(null);
        });
        return this;
    }

    public void Enter() => Enters++;

    public void Leave() => Leaves++;

    public void Draw(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TaskCompletionSource signal;
        lock (_gate)
        {
            _frames.Add(frame);
            signal = _drawn;
            _drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.SetResult();
    }

    public async Task<KeyInput?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (_steps.TryDequeue(out Func<CancellationToken, Task<KeyInput?>>? step))
        {
            if (await step(cancellationToken) is { } key)
            {
                return key;
            }
        }

        return null;
    }
}
