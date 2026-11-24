namespace MySync.Cli;

/// <summary>The command names <c>Program</c> registers, to reject a typo before ConsoleAppFramework turns it into "show help, exit 0".</summary>
internal static class KnownCommands
{
    public static IReadOnlyList<string> Names { get; } = ["sync", "status", "info", "conflicts", "resolve", "gc", "delete", "update", "self-check", "debug", "publish", "publications", "unpublish", "shares", "rename", "diff", "compression", "push", "pull", "tui", "clone"];

    // Options that take a value, so their value isn't mistaken for a command or a path.
    private static readonly string[] OptionsWithValues = ["--parallel", "--keep", "--keep-revisions", "--grace-hours", "--share", "--revision", "--title", "--name", "--context", "-U", "--print", "--host", "--updated-since", "--include", "--exclude", "--max-size", "--to"];

    // Repeatable options, per command: every value is gathered into one JSON array (see ArrangeForParsing).
    private static readonly Dictionary<string, string[]> RepeatableOptions = new(StringComparer.Ordinal)
    {
        ["push"] = ["--include", "--exclude"],
        ["pull"] = ["--host"],
    };

    /// <summary>
    /// <c>resolve</c>, <c>diff</c>, <c>push</c> and <c>pull</c> take any number of paths, and ConsoleAppFramework
    /// gives a <c>params</c> argument every token after the first one, options included. Moving the
    /// options in front (arguments keep their order) lets people write
    /// <c>mysync resolve a.txt b.txt --keep local</c>, <c>mysync diff a.txt --stat</c> or
    /// <c>mysync push logs a.txt b/ --host web01</c>. Other commands are untouched.
    /// <para>
    /// <c>push</c>'s <c>--include</c> and <c>--exclude</c>, and <c>pull</c>'s <c>--host</c>, may be repeated, which ConsoleAppFramework
    /// doesn't collect, and it reads an array option's value as JSON when it starts with <c>[</c> and
    /// as comma-separated otherwise: either would break a pattern (<c>[ab]*.log</c>, <c>a,b.txt</c>).
    /// So each one's values are gathered, in order, into a single JSON array.
    /// </para>
    /// </summary>
    public static string[] ArrangeForParsing(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int command = Array.FindIndex(args, arg => arg is "resolve" or "diff" or "push" or "pull");
        if (command < 0)
        {
            return args;
        }

        string[] repeatable = RepeatableOptions.GetValueOrDefault(args[command]) ?? [];
        List<string> options = [];
        List<string> paths = [];
        Dictionary<string, List<string>> repeated = new(StringComparer.Ordinal);
        string? repeatedOption = null;
        bool isValue = false;
        foreach (string arg in args.Skip(command + 1))
        {
            if (repeatedOption is not null)
            {
                repeated[repeatedOption].Add(arg);
                repeatedOption = null;
            }
            else if (isValue)
            {
                options.Add(arg);
                isValue = false;
            }
            else if (repeatable.Contains(arg, StringComparer.Ordinal))
            {
                repeated.TryAdd(arg, []);
                repeatedOption = arg;
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

        // An option given last without a value keeps it missing, so ConsoleAppFramework reports it.
        foreach ((string option, List<string> values) in repeated)
        {
            options.Add(option);
            if (values.Count > 0 || repeatedOption != option)
            {
                options.Add(JsonArray(values));
            }
        }

        return [.. args[..(command + 1)], .. options, .. paths];
    }

    private static string JsonArray(List<string> values)
    {
        using MemoryStream buffer = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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
