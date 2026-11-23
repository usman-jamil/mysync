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

    [Theory]
    [InlineData("push", "{name}", "app", "--exclude", "*.gz", "--exclude", "*.tmp", "--exclude", "d,e.txt", "--host", "web01")]
    [InlineData("push", "--exclude", "*.gz", "{name}", "--host", "web01", "--exclude", "*.tmp", "app", "--exclude", "d,e.txt")]
    [InlineData("push", "--exclude", "*.gz", "--exclude", "*.tmp", "--exclude", "d,e.txt", "--host", "web01", "{name}", "app")]
    public async Task RepeatedExcludes_Should_AllApply_WhereverTheyGo(params string[] arguments)
    {
        await CreateShareAsync();
        _server.WriteFile("app/a.log", "a").WriteFile("app/b.gz", "b").WriteFile("app/c.tmp", "c").WriteFile("app/d,e.txt", "d").WriteFile("app/k.log", "k");

        CliResult pushed = await RunAsync(_server, [.. arguments.Select(argument => argument.Replace("{name}", _name, StringComparison.Ordinal))]);

        pushed.ExitCode.ShouldBe(0, pushed.ToString());
        pushed.StdOut.ShouldContain($"Pushed 2 files (2 B; 2 uploaded, 0 already stored) to {_name}");
        pushed.StdOut.ShouldContain("3 files left out by filters (3 excluded)");
        pushed.StdOut.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Patterns_Should_ReachMysyncUnchanged_BracketsAndCommasIncluded()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a").WriteFile("b.log", "b").WriteFile("c.log", "c").WriteFile("x,y.log", "x");

        CliResult pushed = await RunAsync(_server, "push", _name, "--include", "[ab].log", "--include", "x,y.log", "--json");

        pushed.ExitCode.ShouldBe(0, pushed.ToString());
        using var json = JsonDocument.Parse(pushed.StdOut);
        JsonElement root = json.RootElement;
        root.GetProperty("files").GetInt32().ShouldBe(3);
        root.GetProperty("filters").GetProperty("include").EnumerateArray().Select(item => item.GetString()).ShouldBe(["[ab].log", "x,y.log"]);
        root.GetProperty("filteredOut").GetProperty("notIncluded").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("--updated-since", "2x", "--updated-since '2x' isn't something mysync understands")]
    [InlineData("--updated-since", "0h", "Use a duration (30m, 2h, 1d, 1w)")]
    [InlineData("--updated-since", "2999-01-01", "is in the future")]
    [InlineData("--include", "!*.log", "negated patterns aren't supported")]
    [InlineData("--exclude", "", "--exclude '' can't be used: it is empty.")]
    [InlineData("--max-size", "0", "--max-size '0' isn't a size")]
    [InlineData("--max-size", "1XB", "--max-size '1XB' isn't a size")]
    public async Task InvalidFilterValues_Should_ExitTwo_SayingWhatToUse(string option, string value, string message)
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a");

        CliResult result = await RunAsync(_server, "push", _name, option, value);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldContain(message);
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdatedSinceLast_Should_SayWhenThereIsNoEarlierPush_ThenUseIt()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a");
        File.SetLastWriteTimeUtc(_server.Combine("a.log"), DateTime.UtcNow.AddDays(-1));

        CliResult first = await RunAsync(_server, "push", _name, "--updated-since", "last", "--host", "web01");
        CliResult second = await RunAsync(_server, "push", _name, "--updated-since", "LAST", "--host", "web01");

        first.ExitCode.ShouldBe(0, first.ToString());
        first.StdOut.ShouldContain("no earlier push from web01; pushing everything · no files left out by filters");
        second.ExitCode.ShouldBe(0, second.ToString());
        second.StdOut.ShouldContain("Nothing to push (1 file left out by filters).");
        second.StdOut.ShouldContain("changed since ");
        second.StdOut.ShouldContain("1 file left out by filters (1 not changed)");
    }

    [Fact]
    public async Task Json_Should_CarryTheFiltersAndCounts_AndNullFiltersWithoutThem()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a").WriteFile("big.log", new string('x', 3000)).WriteFile("b.gz", "b");

        CliResult filtered = await RunAsync(_server, "push", _name, "--updated-since", "1d", "--exclude", "*.gz", "--max-size", "2K", "--json");
        CliResult plain = await RunAsync(_server, "push", _name, "--json");

        filtered.ExitCode.ShouldBe(0, filtered.ToString());
        using (var json = JsonDocument.Parse(filtered.StdOut))
        {
            JsonElement filters = json.RootElement.GetProperty("filters");
            filters.GetProperty("updatedSince").GetString()!.ShouldEndWith("Z");
            filters.GetProperty("updatedSinceSpec").GetString().ShouldBe("1d");
            filters.GetProperty("include").GetArrayLength().ShouldBe(0);
            filters.GetProperty("exclude").EnumerateArray().Select(item => item.GetString()).ShouldBe(["*.gz"]);
            filters.GetProperty("maxSizeBytes").GetInt64().ShouldBe(2048);
            JsonElement counts = json.RootElement.GetProperty("filteredOut");
            (counts.GetProperty("total").GetInt32(), counts.GetProperty("excluded").GetInt32(), counts.GetProperty("tooLarge").GetInt32()).ShouldBe((2, 1, 1));
            json.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
        }

        using (var json = JsonDocument.Parse(plain.StdOut))
        {
            json.RootElement.GetProperty("filters").ValueKind.ShouldBe(JsonValueKind.Null);
            json.RootElement.GetProperty("filteredOut").GetProperty("total").GetInt32().ShouldBe(0);
            json.RootElement.GetProperty("files").GetInt32().ShouldBe(3);
        }
    }

    [Fact]
    public async Task DryRun_WithFilters_Should_ListWhatWouldGo()
    {
        await CreateShareAsync();
        _server.WriteFile("logs/a.log", "a").WriteFile("logs/[odd].log", "o").WriteFile("notes.txt", "n");

        CliResult dryRun = await RunAsync(_server, "push", _name, "-n", "--include", "*.log", "--host", "web01");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        dryRun.StdOut.ShouldContain("Would push 2 files (2 B)");
        dryRun.StdOut.ShouldContain("1 file left out by filters (1 not included)");
        dryRun.StdOut.ShouldContain("    logs/[odd].log");
        dryRun.StdOut.ShouldContain("    logs/a.log");
        dryRun.StdOut.ShouldNotContain("notes.txt");
    }

    [Fact]
    public async Task ANamedFileLeftOutByAFilter_Should_WarnOnStderr()
    {
        await CreateShareAsync();
        _server.WriteFile("a.log", "a").WriteFile("b.gz", "b");

        CliResult result = await RunAsync(_server, "push", _name, "a.log", "b.gz", "--exclude", "*.gz");

        result.ExitCode.ShouldBe(0, result.ToString());
        result.StdErr.ShouldContain("'b.gz' was named, but --exclude '*.gz' left it out, so it wasn't pushed.");
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
