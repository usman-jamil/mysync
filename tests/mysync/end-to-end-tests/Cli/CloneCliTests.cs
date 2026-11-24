using System.Text.Json;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// <c>mysync clone</c> as a process against Azurite (CLAUDE.md §9): the folder created for the
/// share, a later sync in it both ways, by ID into a folder, the refusals, <c>--json</c>, and the
/// exit codes. This is the real composition root building the folder's services again.
/// </summary>
public sealed class CloneCliTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly TempShareFolder _laptop = new();
    private readonly TempShareFolder _server = new();
    private readonly string _container = AzuriteEnvironment.NewContainerName();
    private readonly string _name = "clone-" + Guid.NewGuid().ToString("N")[..12];

    public CloneCliTests(AzuriteFixture azurite) => _azurite = azurite;

    private async Task<string> CreateShareAsync()
    {
        _laptop.WriteFile("docs/readme.md", "# readme\n").WriteFile("config/app.yml", "port: 80\n");
        CliResult created = await RunAsync(_laptop.Path, "sync", "--name", _name, "--json");
        created.ExitCode.ShouldBe(0, created.ToString());
        using var json = JsonDocument.Parse(created.StdOut);
        json.RootElement.GetProperty("cloneCommand").GetString().ShouldBe($"mysync clone {_name}");
        return json.RootElement.GetProperty("shareId").GetString()!;
    }

    [Fact]
    public async Task Clone_Should_CreateTheFolder_AndALaterSyncThere_Should_WorkBothWays()
    {
        string shareId = await CreateShareAsync();

        CliResult cloned = await RunAsync(_server.Path, "clone", _name);

        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        cloned.StdOut.ShouldContain($"Cloned share {_name} ({shareId}) into .{Path.DirectorySeparatorChar}{_name}");
        cloned.StdOut.ShouldContain($"Next:  cd {_name}");
        cloned.StdOut.ShouldNotContain("\u001b");
        string folder = _server.Combine(_name);
        (await File.ReadAllTextAsync(Path.Combine(folder, "docs", "readme.md"), TestContext.Current.CancellationToken)).ShouldBe("# readme\n");
        File.Exists(Path.Combine(folder, StateFileNames.State)).ShouldBeTrue();
        _server.Exists(StateFileNames.State).ShouldBeFalse();
        _server.Exists(StateFileNames.Lock).ShouldBeFalse();

        await File.WriteAllTextAsync(Path.Combine(folder, "from-server.txt"), "server\n", TestContext.Current.CancellationToken);
        (await RunAsync(folder, "sync")).ExitCode.ShouldBe(0);
        (await RunAsync(_laptop.Path, "sync")).ExitCode.ShouldBe(0);
        _laptop.ReadText("from-server.txt").ShouldBe("server\n");

        _laptop.WriteFile("from-laptop.txt", "laptop\n");
        (await RunAsync(_laptop.Path, "sync")).ExitCode.ShouldBe(0);
        (await RunAsync(folder, "sync")).ExitCode.ShouldBe(0);
        (await File.ReadAllTextAsync(Path.Combine(folder, "from-laptop.txt"), TestContext.Current.CancellationToken)).ShouldBe("laptop\n");
    }

    [Fact]
    public async Task CloneById_IntoAFolder_Should_PrintItAndTheCdHint_Quoted()
    {
        string shareId = await CreateShareAsync();
        string folder = Path.Combine("deploy", "my config");

        CliResult cloned = await RunAsync(_server.Path, "clone", shareId, folder);

        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        cloned.StdOut.ShouldContain($"into .{Path.DirectorySeparatorChar}{folder}");
        cloned.StdOut.ShouldContain($"Next:  cd '{folder}'");
        File.Exists(_server.Combine("deploy/my config/config/app.yml")).ShouldBeTrue();
    }

    [Fact]
    public async Task CloneIntoTheCurrentFolder_Should_GiveNoCdHint()
    {
        await CreateShareAsync();

        CliResult cloned = await RunAsync(_server.Path, "clone", _name, ".");

        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        cloned.StdOut.ShouldContain("into .");
        cloned.StdOut.ShouldNotContain("Next:");
        _server.Exists(StateFileNames.State).ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondClone_IntoTheNowFullFolder_Should_BeRefused_WithExitFour()
    {
        await CreateShareAsync();
        (await RunAsync(_server.Path, "clone", _name)).ExitCode.ShouldBe(0);

        CliResult again = await RunAsync(_server.Path, "clone", _name);

        // Its .mysynctool makes it a share; the "inside a share" check sees that first.
        again.ExitCode.ShouldBe(4, again.ToString());
        again.StdErr.ShouldContain(_name);
        again.StdErr.ShouldContain("already syncs with share");
    }

    [Fact]
    public async Task ANonEmptyFolder_Should_BeRefused_WithExitFour_AndLeftAlone()
    {
        await CreateShareAsync();
        _server.WriteFile($"{_name}/mine.txt", "mine");

        CliResult refused = await RunAsync(_server.Path, "clone", _name);

        refused.ExitCode.ShouldBe(4, refused.ToString());
        refused.StdErr.ShouldContain("is not empty");
        Directory.EnumerateFileSystemEntries(_server.Combine(_name)).Select(Path.GetFileName).ShouldBe(["mine.txt"]);
    }

    [Fact]
    public async Task Json_Should_BeTheCloneReport_WithTheFolder()
    {
        string shareId = await CreateShareAsync();

        CliResult cloned = await RunAsync(_server.Path, "clone", _name, "--json");

        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        using var json = JsonDocument.Parse(cloned.StdOut);
        JsonElement root = json.RootElement;
        root.GetProperty("command").GetString().ShouldBe("clone");
        root.GetProperty("shareId").GetString().ShouldBe(shareId);
        root.GetProperty("downloaded").GetProperty("files").GetInt32().ShouldBe(2);
        // Full; the process sees the temp folder through its real path (/private/var on macOS).
        string folder = root.GetProperty("folder").GetString()!;
        Path.IsPathFullyQualified(folder).ShouldBeTrue();
        folder.ShouldEndWith(Path.DirectorySeparatorChar + _name);
        Directory.Exists(folder).ShouldBeTrue();
        root.GetProperty("createdFolder").GetBoolean().ShouldBeTrue();
        root.GetProperty("exitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Json_IntoAnExistingEmptyFolder_Should_SayItWasntCreated()
    {
        await CreateShareAsync();
        _server.CreateDirectory("target");

        CliResult cloned = await RunAsync(_server.Path, "clone", _name, "target", "--json");

        using var json = JsonDocument.Parse(cloned.StdOut);
        json.RootElement.GetProperty("createdFolder").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SyncJson_Should_NotGainTheCloneFields()
    {
        await CreateShareAsync();

        CliResult synced = await RunAsync(_laptop.Path, "sync", "--json");

        using var json = JsonDocument.Parse(synced.StdOut);
        json.RootElement.TryGetProperty("folder", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("createdFolder", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task NoShare_Should_ExitTwo()
    {
        CliResult result = await RunAsync(_server.Path, "clone");

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldContain("mysync clone <name|shareId> [<folder>]");
    }

    [Fact]
    public async Task AnUnknownName_Should_ExitEight_AndCreateNoFolder()
    {
        CliResult result = await RunAsync(_server.Path, "clone", "no-such-share-here");

        result.ExitCode.ShouldBe(8, result.ToString());
        Directory.EnumerateFileSystemEntries(_server.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileInTheWay_Should_ExitFour()
    {
        await CreateShareAsync();
        _server.WriteFile(_name, "a file");

        CliResult result = await RunAsync(_server.Path, "clone", _name, "--json");

        result.ExitCode.ShouldBe(4, result.ToString());
        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("Clone.TargetIsAFile");
    }

    [Fact]
    public async Task CloneInsideAShare_Should_ExitFour()
    {
        await CreateShareAsync();

        CliResult result = await RunAsync(_laptop.Path, "clone", _name, "inner");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("syncs with share");
        _laptop.Exists("inner").ShouldBeFalse();
    }

    [Fact]
    public async Task SyncWithAShare_InAnEmptyFolder_Should_StillClone()
    {
        await CreateShareAsync();

        CliResult cloned = await RunAsync(_server.Path, "sync", _name);

        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        cloned.StdOut.ShouldNotContain(" into ");
        _server.ReadText("config/app.yml").ShouldBe("port: 80\n");
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _server.Dispose();
    }

    private Task<CliResult> RunAsync(string folder, params string[] arguments) =>
        CliProcess.RunAsync(folder, new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = _azurite.ConnectionString,
            ["MYSYNC_CONTAINER"] = _container,
        }, arguments);
}
