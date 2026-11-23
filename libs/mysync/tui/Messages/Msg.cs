using MySync.Application.Conflicts.Diff;
using MySync.Application.Session;
using SharedKernel;

namespace MySync.Tui.Messages;

/// <summary>The keys the interface knows. Letters, digits and symbols arrive as <see cref="Character"/>.</summary>
public enum KeyCode
{
    Character = 0,
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4,
    PageUp = 5,
    PageDown = 6,
    Home = 7,
    End = 8,
    Enter = 9,
    Escape = 10,
    Tab = 11,
    Backspace = 12,
    Delete = 13,
    CtrlC = 14,
}

/// <summary>One key press.</summary>
public readonly record struct KeyInput(KeyCode Code, char Character = '\0', bool Shift = false)
{
    public static KeyInput Of(char c) => new(KeyCode.Character, c);

    public static KeyInput Of(KeyCode code, bool shift = false) => new(code, '\0', shift);

    public bool Is(char c) => Code == KeyCode.Character && Character == c;
}

/// <summary>Everything Update reacts to. Time arrives as a message, never from a clock.</summary>
public abstract record Msg;

public sealed record KeyPressed(KeyInput Key) : Msg;

public sealed record Resized(int Width, int Height) : Msg;

public sealed record SessionMsg(SessionEvent Event) : Msg;

public sealed record Tick(DateTimeOffset Now) : Msg;

/// <summary>The diff of a path came back (or failed).</summary>
public sealed record DiffLoaded(string Path, DiffResponse? Diff, Error? Error) : Msg;

/// <summary>The session refused to start a job (busy, an invalid push, …).</summary>
public sealed record Refused(Error Error) : Msg;

/// <summary>A warning or error the log wrote while the screen was open.</summary>
public sealed record LogLine(string Text) : Msg;
