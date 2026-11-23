using System.Runtime.InteropServices;

namespace MySync.Tui.Terminal;

/// <summary>The process's own console. On Windows it turns on virtual-terminal processing (and back off at the end).</summary>
public sealed partial class SystemConsole : IConsole
{
    private const int StdOutputHandle = -11;
    private const int StdInputHandle = -10;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;

    private uint? _savedOutputMode;
    private uint? _savedInputMode;

    public TextWriter Out => Console.Out;

    public int Width => Console.WindowWidth;

    public int Height => Console.WindowHeight;

    public bool KeyAvailable => Console.KeyAvailable;

    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;
        set => Console.TreatControlCAsInput = value;
    }

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        IntPtr output = NativeMethods.GetStdHandle(StdOutputHandle);
        IntPtr input = NativeMethods.GetStdHandle(StdInputHandle);
        if (NativeMethods.GetConsoleMode(input, out uint inputMode))
        {
            _savedInputMode = inputMode;
        }

        if (!NativeMethods.GetConsoleMode(output, out uint outputMode))
        {
            return false;
        }

        _savedOutputMode = outputMode;
        return NativeMethods.SetConsoleMode(output, outputMode | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
    }

    public void RestoreMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_savedOutputMode is { } output)
        {
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(StdOutputHandle), output);
        }

        if (_savedInputMode is { } input)
        {
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(StdInputHandle), input);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial IntPtr GetStdHandle(int handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetConsoleMode(IntPtr handle, out uint mode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetConsoleMode(IntPtr handle, uint mode);
    }
}
