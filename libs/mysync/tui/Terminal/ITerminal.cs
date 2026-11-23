using MySync.Tui.Messages;
using MySync.Tui.Rendering;

namespace MySync.Tui.Terminal;

/// <summary>What the runtime needs from a terminal. <see cref="AnsiTerminal"/> is the one real one; tests script their own.</summary>
public interface ITerminal
{
    /// <summary>The window's size now, in cells (polled for resizes).</summary>
    (int Width, int Height) Size { get; }

    ColorDepth Colors { get; }

    /// <summary>Takes the screen: alternate buffer, cursor hidden, keys without echo.</summary>
    void Enter();

    /// <summary>Gives the screen back exactly as it was. Safe to call more than once.</summary>
    void Leave();

    /// <summary>Draws a frame (only what changed since the last one).</summary>
    void Draw(Frame frame);

    /// <summary>The next key; <c>null</c> when input has ended.</summary>
    Task<KeyInput?> ReadKeyAsync(CancellationToken cancellationToken);
}

/// <summary>The console as <see cref="AnsiTerminal"/> uses it, so its output can be tested against a buffer.</summary>
public interface IConsole
{
    TextWriter Out { get; }

    int Width { get; }

    int Height { get; }

    bool KeyAvailable { get; }

    ConsoleKeyInfo ReadKey();

    /// <summary>Ctrl+C arrives as a key, not a signal, while the screen is open.</summary>
    bool TreatControlCAsInput { get; set; }

    /// <summary>Turns on virtual-terminal processing (Windows); <c>false</c> when the console can't.</summary>
    bool EnableVirtualTerminal();

    void RestoreMode();
}
