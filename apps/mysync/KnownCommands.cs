namespace MySync.Cli;

/// <summary>The command names <c>Program</c> registers, to reject a typo before ConsoleAppFramework turns it into "show help, exit 0".</summary>
internal static class KnownCommands
{
    public static IReadOnlyList<string> Names { get; } = ["sync", "self-check"];

    // Global options that take a value, so their value isn't mistaken for a command.
    private static readonly string[] OptionsWithValues = ["--parallel"];

    /// <summary>The first non-option argument if it isn't a command, else null (no command at all shows the help).</summary>
    public static string? FindUnknown(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool isValue = false;
        foreach (string arg in args)
        {
            if (isValue)
            {
                isValue = false;
                continue;
            }

            if (OptionsWithValues.Contains(arg, StringComparer.Ordinal))
            {
                isValue = true;
                continue;
            }

            if (!arg.StartsWith('-'))
            {
                return Names.Contains(arg, StringComparer.Ordinal) ? null : arg;
            }
        }

        return null;
    }
}
