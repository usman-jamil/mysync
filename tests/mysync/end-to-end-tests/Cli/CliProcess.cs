using System.Diagnostics;
using System.Text;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>What one run of <c>mysync</c> left behind: exit code, stdout and stderr, kept apart.</summary>
internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout\n{StdOut}\n--- stderr\n{StdErr}";
}

/// <summary>
/// Runs the built <c>mysync</c> as a separate process, the only honest way to check exit codes,
/// the stdout/stderr split and how it behaves without a terminal (CLAUDE.md §4, §10). The
/// environment is built from scratch: the developer's own <c>AZURE_STORAGE_CONNECTION_STRING</c>
/// and <c>MYSYNC_*</c> variables never reach it.
/// </summary>
internal static class CliProcess
{
    private static readonly Lazy<string> CliPath = new(FindCli);

    public static async Task<CliResult> RunAsync(string workingDirectory, IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        (await RunForBytesAsync(workingDirectory, environment, arguments)).Result;

    /// <summary>
    /// A run inside a pseudo-terminal of <paramref name="columns"/> × <paramref name="rows"/>
    /// (<c>script</c>, on Linux and macOS), for <c>mysync tui</c>'s start-up checks. Stdout and stderr
    /// arrive together, as on a real terminal. <c>null</c> where there is no <c>script</c> (Windows).
    /// </summary>
    public static async Task<CliResult?> RunInTerminalAsync(string workingDirectory, IReadOnlyDictionary<string, string?> environment, int columns, int rows, params string[] arguments)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/script"))
        {
            return null;
        }

        static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        string command = string.Join(' ', new[] { Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", CliPath.Value }.Concat(arguments).Select(Quote));
        string shell = $"stty cols {columns} rows {rows}; exec {command}";
        string[] script = OperatingSystem.IsMacOS()
            ? ["-q", "/dev/null", "/bin/sh", "-c", shell]
            : ["-qec", shell, "/dev/null"];
        return await RunProgramAsync("/usr/bin/script", workingDirectory, environment, script);
    }

    private static async Task<CliResult> RunProgramAsync(string program, string workingDirectory, IReadOnlyDictionary<string, string?> environment, string[] arguments)
    {
        ProcessStartInfo start = new(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("MYSYNC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(name);
            }
        }

        foreach ((string name, string? value) in environment)
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CliResult(process.ExitCode, (await stdout).Replace("\r\n", "\n", StringComparison.Ordinal), await stderr);
    }

    /// <summary>The same run, with stdout also kept as raw bytes (for <c>diff --print</c>).</summary>
    public static async Task<(CliResult Result, byte[] StdOut)> RunForBytesAsync(string workingDirectory, IReadOnlyDictionary<string, string?> environment, params string[] arguments)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath.Value);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("MYSYNC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(name);
            }
        }

        foreach ((string name, string? value) in environment)
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start)!;
        process.StandardInput.Close(); // no keyboard: a prompt must never wait for one
        Task<byte[]> stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        byte[] raw = await stdout;
        return (new CliResult(process.ExitCode, Encoding.UTF8.GetString(raw), await stderr), raw);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes, TestContext.Current.CancellationToken);
        return bytes.ToArray();
    }

    // <repo>/tests/.../bin/<Configuration>/<framework>/ → <repo>/apps/mysync/bin/<Configuration>/<framework>/mysync.dll
    private static string FindCli()
    {
        DirectoryInfo output = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string framework = output.Name;
        string configuration = output.Parent!.Name;
        DirectoryInfo repository = output;
        while (!File.Exists(Path.Combine(repository.FullName, "mysync.slnx")))
        {
            repository = repository.Parent ?? throw new DirectoryNotFoundException("mysync.slnx not found above the test output.");
        }

        string cli = Path.Combine(repository.FullName, "apps", "mysync", "bin", configuration, framework, "mysync.dll");

        return File.Exists(cli) ? cli : throw new FileNotFoundException($"Build the CLI first: {cli} is missing.", cli);
    }
}
