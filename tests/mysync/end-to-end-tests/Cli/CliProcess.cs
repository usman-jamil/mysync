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

    public static async Task<CliResult> RunAsync(string workingDirectory, IReadOnlyDictionary<string, string?> environment, params string[] arguments)
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

        return new CliResult(process.ExitCode, await stdout, await stderr);
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
