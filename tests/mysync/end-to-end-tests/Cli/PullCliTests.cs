using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySync.Domain.Content;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// <c>mysync pull</c> as a process (CLAUDE.md §10c): where its options may go, repeated
/// <c>--host</c>, plain output when nothing is a terminal, <c>--json</c>, and its exit codes
/// (0, 2, 4, 8, 9; never 3, 5 or 7).
/// </summary>
public sealed class PullCliTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly TempShareFolder _laptop = new();
    private readonly TempShareFolder _server = new();
    private readonly TempShareFolder _collector = new();
    private readonly string _container = AzuriteEnvironment.NewContainerName();
    private readonly string _name = "pull-" + Guid.NewGuid().ToString("N")[..12];

    public PullCliTests(AzuriteFixture azurite) => _azurite = azurite;

    private async Task<string> CreateShareAsync()
    {
        _laptop.WriteFile("docs/readme.md", "# readme\n").WriteFile("docs/guide.md", "guide\n").WriteFile("config/app.yml", "port: 80\n");
        CliResult created = await RunAsync(_laptop, "sync", "--name", _name, "--json");
        created.ExitCode.ShouldBe(0, created.ToString());
        using var json = JsonDocument.Parse(created.StdOut);
        return json.RootElement.GetProperty("shareId").GetString()!;
    }

    private async Task PushAsync(string host)
    {
        _server.WriteFile("app/a.log", $"{host}\n");
        CliResult pushed = await RunAsync(_server, "push", _name, "--host", host);
        pushed.ExitCode.ShouldBe(0, pushed.ToString());
    }

    [Theory]
    [InlineData("pull", "{name}", "docs", "--to", "out")]
    [InlineData("pull", "--to", "out", "{name}", "docs")]
    [InlineData("pull", "{name}", "--to", "out", "docs")]
    public async Task Pull_Should_PrintTheSummary_WhereverItsOptionsGo(params string[] arguments)
    {
        await CreateShareAsync();

        CliResult pulled = await RunAsync(_collector, [.. arguments.Select(argument => argument.Replace("{name}", _name, StringComparison.Ordinal))]);

        pulled.ExitCode.ShouldBe(0, pulled.ToString());
        pulled.StdOut.ShouldContain($"Pulled 2 files (15 B) from {_name}, revision 1");
        pulled.StdOut.ShouldContain($"into:   .{Path.DirectorySeparatorChar}out{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}");
        pulled.StdOut.ShouldNotContain("\u001b");
        pulled.StdErr.ShouldNotContain("\u001b");
        _collector.ReadText("out/docs/readme.md").ShouldBe("# readme\n");
        _collector.Exists(StateFileNames.State).ShouldBeFalse();
    }

    [Fact]
    public async Task RepeatedHosts_Should_AllCount_AndJsonDescribesThePull()
    {
        await CreateShareAsync();
        await PushAsync("web01");
        await PushAsync("web02");
        await PushAsync("db01");

        CliResult pulled = await RunAsync(_collector, "pull", _name, "--latest", "--host", "web01", "--json", "--host", "WEB02");

        pulled.ExitCode.ShouldBe(0, pulled.ToString());
        using var json = JsonDocument.Parse(pulled.StdOut);
        JsonElement root = json.RootElement;
        root.GetProperty("command").GetString().ShouldBe("pull");
        root.GetProperty("shareName").GetString().ShouldBe(_name);
        root.GetProperty("revision").GetInt64().ShouldBe(4);
        root.GetProperty("destination").GetString()!.ShouldEndWith(Path.GetFileName(Path.TrimEndingDirectorySeparator(_collector.Path))); // the temp folder may be reached through a symlink
        root.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        root.GetProperty("latest").GetBoolean().ShouldBeTrue();
        string[] selected = [.. root.GetProperty("selected").EnumerateArray().Select(item => item.GetProperty("localPath").GetString()!)];
        selected.Length.ShouldBe(2);
        selected[0].ShouldStartWith("web01/");
        selected[1].ShouldStartWith("web02/");
        root.GetProperty("files").GetInt32().ShouldBe(4); // a.log and the push record, from each
        root.GetProperty("downloaded").GetProperty("files").GetInt32().ShouldBe(4);
        root.GetProperty("alreadyThere").GetProperty("files").GetInt32().ShouldBe(0);
        root.GetProperty("overwritten").GetInt32().ShouldBe(0);
        root.GetProperty("skipped").GetArrayLength().ShouldBe(0);
        root.GetProperty("warnings").GetArrayLength().ShouldBe(0);
        root.GetProperty("exitCode").GetInt32().ShouldBe(0);
        _collector.ReadText($"{selected[1]}/app/a.log").ShouldBe("web02\n");
    }

    [Fact]
    public async Task ARerun_Should_FindEverythingAlreadyThere()
    {
        await CreateShareAsync();
        (await RunAsync(_collector, "pull", _name)).ExitCode.ShouldBe(0);

        CliResult again = await RunAsync(_collector, "pull", _name);

        again.ExitCode.ShouldBe(0, again.ToString());
        again.StdOut.ShouldContain("downloaded 0, 3 already there");
    }

    [Fact]
    public async Task DryRun_Should_SayWhatWouldBePulled_AndWriteNothing()
    {
        await CreateShareAsync();

        CliResult dryRun = await RunAsync(_collector, "pull", _name, "-n", "--to", "new-folder");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        dryRun.StdOut.ShouldContain($"Would pull 3 files (24 B) from {_name}, revision 1 — nothing was written.");
        _collector.Exists("new-folder").ShouldBeFalse();
    }

    [Fact]
    public async Task AMissingObject_Should_SkipThatFile_AndExitNine()
    {
        string shareId = await CreateShareAsync();
        using AzureMachine admin = new(_azurite.ConnectionString, _container);
        var guide = ContentHash.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes("guide\n")));
        ShareId id = ShareId.TryParse(shareId, out ShareId? parsed) ? parsed : throw new InvalidOperationException(shareId);
        bool deleted = await admin.WithStoreAsync(async store => (await store.DeleteObjectAsync(id, guide, TestContext.Current.CancellationToken)).IsSuccess);
        deleted.ShouldBeTrue();

        CliResult result = await RunAsync(_collector, "pull", _name);

        result.ExitCode.ShouldBe(9, result.ToString());
        result.StdErr.ShouldContain("1 path was not pulled:");
        result.StdErr.ShouldContain("docs/guide.md");
        _collector.ReadText("docs/readme.md").ShouldBe("# readme\n");
    }

    [Theory]
    [InlineData("--latest", "docs")]
    [InlineData("--host", "web01")]
    [InlineData("nope")]
    [InlineData("docs/readme.md", "config/../docs/readme.md")]
    public async Task UsageErrors_Should_ExitTwo(params string[] arguments)
    {
        await CreateShareAsync();

        CliResult result = await RunAsync(_collector, ["pull", _name, .. arguments]);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdOut.ShouldBeEmpty();
        Directory.EnumerateFileSystemEntries(_collector.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMissingShareArgument_AndAnUnknownHost_Should_ExitTwo()
    {
        await CreateShareAsync();
        await PushAsync("web01");

        CliResult noShare = await RunAsync(_collector, "pull");
        CliResult noHost = await RunAsync(_collector, "pull", _name, "--latest", "--host", "db01");

        noShare.ExitCode.ShouldBe(2, noShare.ToString());
        noShare.StdErr.ShouldContain("Name the share to pull from");
        noHost.ExitCode.ShouldBe(2, noHost.ToString());
        noHost.StdErr.ShouldContain("'db01'");
    }

    [Fact]
    public async Task ADifferingLocalFile_Should_ExitFour_AndOverwriteReplaceIt()
    {
        await CreateShareAsync();
        _collector.WriteFile("docs/readme.md", "mine\n");

        CliResult refused = await RunAsync(_collector, "pull", _name);
        CliResult replaced = await RunAsync(_collector, "pull", _name, "--overwrite");

        refused.ExitCode.ShouldBe(4, refused.ToString());
        refused.StdErr.ShouldContain("'docs/readme.md'");
        replaced.ExitCode.ShouldBe(0, replaced.ToString());
        replaced.StdOut.ShouldContain("1 replaced (--overwrite)");
        _collector.ReadText("docs/readme.md").ShouldBe("# readme\n");
    }

    [Fact]
    public async Task PullingIntoTheSharesOwnFolder_Should_ExitFour()
    {
        await CreateShareAsync();

        CliResult result = await RunAsync(_laptop, "pull", _name, "--to", "copy");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("already syncs with");
    }

    [Fact]
    public async Task NoPushes_AndAMissingShare_Should_ExitEight()
    {
        await CreateShareAsync();

        CliResult noPushes = await RunAsync(_collector, "pull", _name, "--latest");
        CliResult missing = await RunAsync(_collector, "pull", "no-such-share-" + Guid.NewGuid().ToString("N")[..8]);

        noPushes.ExitCode.ShouldBe(8, noPushes.ToString());
        noPushes.StdErr.ShouldContain("has no pushed folders");
        missing.ExitCode.ShouldBe(8, missing.ToString());
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _server.Dispose();
        _collector.Dispose();
    }

    private Task<CliResult> RunAsync(TempShareFolder folder, params string[] arguments) =>
        CliProcess.RunAsync(folder.Path, new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = _azurite.ConnectionString,
            ["MYSYNC_CONTAINER"] = _container,
        }, arguments);
}
