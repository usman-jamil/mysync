using System.Globalization;
using System.Text;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// <c>mysync tui</c>'s start-up checks as a process (CLAUDE.md §10d): no terminal, a window too
/// small, no name in a new folder, a taken name, a second instance. The ones past the terminal
/// check run inside a pseudo-terminal (<c>script</c>, Linux and macOS).
/// </summary>
public sealed class TuiCliTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly TempShareFolder _folder = new();
    private readonly TempShareFolder _other = new();
    private readonly string _container = AzuriteEnvironment.NewContainerName();
    private readonly string _name = "tui-" + Guid.NewGuid().ToString("N")[..12];

    public TuiCliTests(AzuriteFixture azurite) => _azurite = azurite;

    private Dictionary<string, string?> Variables() => new()
    {
        ["AZURE_STORAGE_CONNECTION_STRING"] = _azurite.ConnectionString,
        ["MYSYNC_CONTAINER"] = _container,
        ["TERM"] = "xterm-256color",
    };

    private async Task<CliResult> InTerminalAsync(TempShareFolder folder, int columns, int rows, params string[] arguments)
    {
        CliResult? result = await CliProcess.RunInTerminalAsync(folder.Path, Variables(), columns, rows, ["tui", .. arguments]);
        if (result is null)
        {
            Assert.Skip("needs a pseudo-terminal (script), which Windows doesn't have");
        }

        return result;
    }

    [Fact]
    public async Task RedirectedInputOrOutput_Should_ExitTwo_WithoutTheScreen()
    {
        CliResult result = await CliProcess.RunAsync(_folder.Path, Variables(), "tui");

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldContain("mysync tui needs an interactive terminal. Use mysync sync in scripts.");
        result.StdOut.ShouldNotContain("\u001b[?1049h");
    }

    [Fact]
    public async Task ATooSmallWindow_Should_ExitTwo()
    {
        CliResult result = await InTerminalAsync(_folder, 60, 20);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdOut.ShouldContain("at least 80 × 24");
    }

    [Fact]
    public async Task ANewFolderWithoutAName_Should_ExitTwo_AndNeverOpenTheScreen()
    {
        _folder.WriteFile("a.txt", "a");

        CliResult result = await InTerminalAsync(_folder, 100, 30);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdOut.ShouldContain("--name");
        result.StdOut.ShouldContain("mysync clone <name> first");
        result.StdOut.ShouldNotContain("\u001b[?1049h");
        _folder.Exists(StateFileNames.State).ShouldBeFalse();
    }

    [Fact]
    public async Task ATakenName_Should_ExitFour()
    {
        _other.WriteFile("b.txt", "b");
        CliResult created = await CliProcess.RunAsync(_other.Path, Variables(), "sync", "--name", _name);
        created.ExitCode.ShouldBe(0, created.ToString());

        CliResult result = await InTerminalAsync(_folder, 100, 30, "--name", _name);

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdOut.ShouldContain(_name);
        result.StdOut.ShouldNotContain("\u001b[?1049h");
    }

    [Fact]
    public async Task ASecondInstance_Should_ExitSix()
    {
        _other.WriteFile("b.txt", "b");
        (await CliProcess.RunAsync(_other.Path, Variables(), "sync", "--name", _name)).ExitCode.ShouldBe(0);

        // Another mysync tui, as far as this one can tell: the lock file held open, with its process ID.
        await using FileStream held = new(Path.Combine(_other.Path, StateFileNames.InterfaceLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await held.WriteAsync(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)), TestContext.Current.CancellationToken);
        await held.FlushAsync(TestContext.Current.CancellationToken);

        CliResult result = await InTerminalAsync(_other, 100, 30);

        result.ExitCode.ShouldBe(6, result.ToString());
        result.StdOut.ShouldContain("mysync tui is already running in this folder");
    }

    public void Dispose()
    {
        _folder.Dispose();
        _other.Dispose();
    }
}
