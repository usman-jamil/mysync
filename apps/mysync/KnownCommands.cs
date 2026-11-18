namespace MySync.Cli;

/// <summary>The command names <c>Program</c> registers, to reject a typo before ConsoleAppFramework turns it into "show help, exit 0".</summary>
internal static class KnownCommands
{
    public static IReadOnlyList<string> Names { get; } = ["sync", "status", "info", "conflicts", "resolve", "gc", "delete", "self-check"];

    // Options that take a value, so their value isn't mistaken for a command or a path.
    private static readonly string[] OptionsWithValues = ["--parallel", "--keep", "--keep-revisions", "--grace-hours"];

    /// <summary>
    /// <c>resolve</c> takes any number of paths, and ConsoleAppFramework gives a <c>params</c>
    /// argument every token after the first one, options included. Moving the options in front
    /// lets people write <c>mysync resolve a.txt b.txt --keep local</c>. Other commands are untouched.
    /// </summary>
    public static string[] ArrangeForParsing(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int command = Array.IndexOf(args, "resolve");
        if (command < 0)
        {
            return args;
        }

        List<string> options = [];
        List<string> paths = [];
        bool isValue = false;
        foreach (string arg in args.Skip(command + 1))
        {
            if (isValue)
            {
                options.Add(arg);
                isValue = false;
            }
            else if (!arg.StartsWith('-'))
            {
                paths.Add(arg);
            }
            else
            {
                options.Add(arg);
                isValue = OptionsWithValues.Contains(arg, StringComparer.Ordinal);
            }
        }

        return [.. args[..(command + 1)], .. options, .. paths];
    }

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
