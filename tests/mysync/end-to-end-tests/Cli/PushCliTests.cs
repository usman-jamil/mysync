using System.Text.Json;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// <c>mysync push</c> as a process (CLAUDE.md §10b): where its options may go, plain output when
/// nothing is a terminal, <c>--json</c>, and its exit codes (0, 2, 4, 8, 9; never 3 or 7).
/// </summary>
public sealed class PushCliTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly TempShareFolder _laptop = new();
    private readonly TempShareFolder _server = new();
    private readonly string _container = AzuriteEnvironment.NewContainerName();
    private readonly string _name = "push-" + Guid.NewGuid().ToString("N")[..12];

    public PushCliTests(AzuriteFixture azurite) => _azurite = azurite;

    private async Task CreateShareAsync()
    {
        _laptop.WriteFile("notes.md", "# notes");
        CliResult created = await RunAsync(_laptop, "sync", "--name", _name);
        created.ExitCode.ShouldBe(0, created.ToString());
    }

    [Theory]
    [InlineData("push", "{name}", "app", "config.yml", "--host", "web01")] // options after the paths
    [InlineData("push", "--host", "web01", "{name}", "app", "config.yml")] // before the share
    [InlineData("push", "{name}", "--host", "web01", "app", "config.yml")] // in between
    public async Task Push_Should_PrintTheSummary_WhereverItsOptionsGo(params string[] arguments)
    {
        await CreateShareAsync();
        _server.WriteFile("app/error.log", "e\n").WriteFile("app/sub/b.log", "b\n").WriteFile("config.yml", "c\n").WriteFile("other.txt", "not pushed");

        CliResult pushed = await RunAsync(_server, [.. arguments.Select(argument => argument.Replace("{name}", _name, StringComparison.Ordinal))]);

        pushed.ExitCode.ShouldBe(0, pushed.ToString());
        pushed.StdOut.ShouldContain($"Pushed 3 files (6 B; 3 uploaded, 0 already stored) to {_name}");
        pushed.StdOut.ShouldContain("folder:  web01/");
        pushed.StdOut.ShouldContain("revision 2");
        pushed.StdOut.ShouldContain($"Get them with:  mysync sync {_name}");
        pushed.StdOut.ShouldNotContain("\u001b");
        pushed.StdErr.ShouldNotContain("\u001b");
        File.Exists(Path.Combine(_server.Path, StateFileNames.State)).ShouldBeFalse();

        CliResult synced = await RunAsync(_laptop, "sync");
        synced.ExitCode.ShouldBe(0, synced.ToString());
        Directory.GetDirectories(Path.Combine(_laptop.Path, "web01")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Json_Should_DescribeThePush_AndARepeatUploadsNothing()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a\n");

        CliResult first = await RunAsync(_server, "push", _name, "--host", "web01", "--json");
        CliResult second = await RunAsync(_server, "push", _name, "--host", "web01", "--json");

        first.ExitCode.ShouldBe(0, first.ToString());
        using var json = JsonDocument.Parse(second.StdOut);
        JsonElement root = json.RootElement;
        root.GetProperty("command").GetString().ShouldBe("push");
        root.GetProperty("shareName").GetString().ShouldBe(_name);
        root.GetProperty("target").GetString()!.ShouldStartWith("web01/");
        using (var firstJson = JsonDocument.Parse(first.StdOut))
        {
            root.GetProperty("target").GetString().ShouldNotBe(firstJson.RootElement.GetProperty("target").GetString()); // a new folder (-2 within one second)
        }

        root.GetProperty("revision").GetInt64().ShouldBe(3);
        root.GetProperty("committed").GetBoolean().ShouldBeTrue();
        root.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        root.GetProperty("created").GetBoolean().ShouldBeFalse();
        root.GetProperty("files").GetInt32().ShouldBe(1);
        root.GetProperty("uploaded").GetProperty("files").GetInt32().ShouldBe(0);
        root.GetProperty("alreadyStored").GetProperty("files").GetInt32().ShouldBe(1);
        root.GetProperty("commitAttempts").GetInt32().ShouldBe(1);
        root.GetProperty("skipped").GetArrayLength().ShouldBe(0);
        root.GetProperty("ignoredByShare").GetInt32().ShouldBe(0);
        root.GetProperty("exitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task DryRun_Should_SayWhatWouldBePushed_AndChangeNothing()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a\n");

        CliResult dryRun = await RunAsync(_server, "push", _name, "-n", "--host", "web01");
        CliResult info = await RunAsync(_laptop, "info", "--json");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        dryRun.StdOut.ShouldContain($"Would push 1 file (2 B) to {_name}, folder web01/");
        dryRun.StdOut.ShouldContain("— nothing was changed.");
        using var described = JsonDocument.Parse(info.StdOut);
        described.RootElement.GetProperty("remoteRevision").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Create_Should_MakeTheShare()
    {
        _server.WriteFile("a.log", "a\n");

        CliResult created = await RunAsync(_server, "push", _name, "--create", "--host", "web01");

        created.ExitCode.ShouldBe(0, created.ToString());
        created.StdOut.ShouldContain($"Created share {_name} and pushed 1 file");
    }

    [Fact]
    public async Task NothingToPush_Should_ExitZero()
    {
        await CreateShareAsync();

        CliResult result = await RunAsync(_server, "push", _name);

        result.ExitCode.ShouldBe(0, result.ToString());
        result.StdOut.ShouldContain("Nothing to push.");
    }

    [Theory]
    [InlineData("push")]
    [InlineData("push", "Not A Share!")]
    [InlineData("push", "{name}", "missing.log")]
    [InlineData("push", "{name}", "a.log", "./a.log")]
    [InlineData("push", "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", "--create")]
    [InlineData("push", "{name}", "--parallel", "0")]
    [InlineData("push", "{name}", "--host", " ")]
    public async Task UsageErrors_Should_ExitTwo(params string[] arguments)
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a\n");

        CliResult result = await RunAsync(_server, [.. arguments.Select(argument => argument.Replace("{name}", _name, StringComparison.Ordinal))]);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldNotBeEmpty();
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMissingShare_Should_ExitEight_WithHowToCreateIt()
    {
        _server.WriteFile("a.log", "a\n");

        CliResult result = await RunAsync(_server, "push", "no-such-share");

        result.ExitCode.ShouldBe(8, result.ToString());
        result.StdErr.ShouldContain("Create it with: mysync push no-such-share --create, or mysync sync --name no-such-share in a folder");
    }

    [Fact]
    public async Task PushingTheSharesOwnFolder_Should_ExitFour()
    {
        await CreateShareAsync();

        CliResult result = await RunAsync(_laptop, "push", _name, "--force");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("already syncs with");
        result.StdErr.ShouldContain("use mysync sync");
    }

    [Fact]
    public async Task AFileWhereTheHostFolderGoes_Should_ExitFour()
    {
        await CreateShareAsync();
        _laptop.WriteFile("web01", "a file");
        (await RunAsync(_laptop, "sync")).ExitCode.ShouldBe(0);
        _server.WriteFile("a.log", "a\n");

        CliResult result = await RunAsync(_server, "push", _name, "--host", "web01");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("--host");
    }

    [Fact]
    public async Task ASkippedPath_Should_ExitNine_AndStillPushTheRest()
    {
        await CreateShareAsync();
        _server.WriteFile("app/a.log", "a\n").WriteFile("real.txt", "r");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_server.Path, "app", "link.txt"), Path.Combine(_server.Path, "real.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }

        CliResult result = await RunAsync(_server, "push", _name, "app", "--host", "web01");

        result.ExitCode.ShouldBe(9, result.ToString());
        result.StdErr.ShouldContain("app/link.txt");
        result.StdErr.ShouldContain("symlink");
        result.StdOut.ShouldContain("Pushed 1 file");
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _server.Dispose();
    }

    private Task<CliResult> RunAsync(TempShareFolder folder, params string[] arguments) =>
        CliProcess.RunAsync(folder.Path, new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = _azurite.ConnectionString,
            ["MYSYNC_CONTAINER"] = _container,
        }, arguments);
}
