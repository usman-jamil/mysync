using System.Text.Json;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// The real <c>mysync</c> binary, run as a process in temp folders against Azurite: exit codes
/// (CLAUDE.md §10), stdout for output and stderr for everything else, <c>--json</c>, the one
/// prompt, and no secret anywhere. Nothing in <c>apps/mysync/Rendering</c> has unit tests; this is
/// where the CLI's behaviour is held.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly TempShareFolder _a = new();
    private readonly TempShareFolder _b = new();
    private readonly string _container = AzuriteEnvironment.NewContainerName();

    public CliTests(AzuriteFixture azurite) => _azurite = azurite;

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("sync", "--help")]
    public async Task HelpAndVersion_Should_Work_WithNothingConfigured(params string[] arguments)
    {
        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>(), arguments);

        result.ExitCode.ShouldBe(0, result.ToString());
        result.StdOut.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("snyc")]
    [InlineData("sync", "--no-such-option")]
    [InlineData("sync", "--parallel", "0")]
    [InlineData("sync", "not-a-uuid")]
    public async Task UsageErrors_Should_ExitTwo_WithTheMessageOnStderr(params string[] arguments)
    {
        CliResult result = await RunAsync(_a, arguments);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldNotBeEmpty();
        result.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateCloneAndSync_Should_PrintTheShareId_AndExitZero()
    {
        _a.WriteFile("docs/readme.md", "# hi").WriteFile("notes.txt", "n");

        CliResult created = await RunAsync(_a, "sync");
        string shareId = ShareIdOf(_a);
        CliResult cloned = await RunAsync(_b, "sync", shareId);
        _a.WriteFile("notes.txt", "edited on a");
        CliResult synced = await RunAsync(_a, "sync");
        CliResult pulled = await RunAsync(_b, "sync");

        created.ExitCode.ShouldBe(0, created.ToString());
        created.StdOut.ShouldContain($"Created share {shareId} (2 files, 5 B)");
        created.StdOut.ShouldContain($"mysync sync {shareId}");
        cloned.ExitCode.ShouldBe(0, cloned.ToString());
        cloned.StdOut.ShouldContain($"Cloned share {shareId}");
        synced.ExitCode.ShouldBe(0, synced.ToString());
        synced.StdOut.ShouldContain("committed revision 2");
        pulled.ExitCode.ShouldBe(0, pulled.ToString());
        _b.Files().ShouldBe(_a.Files());
    }

    [Theory]
    [InlineData("NOT_A_CI", "1")]
    [InlineData("GITHUB_ACTIONS", "true")] // Spectre turns ANSI on for these unless told otherwise
    [InlineData("TF_BUILD", "True")]
    public async Task Progress_Should_GoToStderr_AsPlainLines_WhenThereIsNoTerminal_EvenInCi(string variable, string value)
    {
        _a.WriteFile("a.txt", "a");

        CliResult created = await RunAsync(_a, new Dictionary<string, string?> { [variable] = value }, "sync");

        created.StdErr.ShouldContain("Scanning the folder…");
        created.StdErr.ShouldContain("↑ Uploading 1 file");
        created.StdOut.ShouldNotContain("Scanning");
        (created.StdOut + created.StdErr).ShouldNotContain("\u001b[", customMessage: "no ANSI escapes when output is redirected");
    }

    [Fact]
    public async Task Json_Should_PrintOneDocumentOnStdout_AndNothingElse()
    {
        _a.WriteFile("a.txt", "a");

        CliResult created = await RunAsync(_a, "sync", "--json");

        created.ExitCode.ShouldBe(0, created.ToString());
        using var json = JsonDocument.Parse(created.StdOut);
        json.RootElement.GetProperty("command").GetString().ShouldBe("create");
        json.RootElement.GetProperty("shareId").GetString().ShouldBe(ShareIdOf(_a));
        json.RootElement.GetProperty("fileCount").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
        created.StdErr.ShouldBeEmpty();
    }

    [Fact]
    public async Task AConflict_Should_ExitThree_AndBeListed_InTextAndJson()
    {
        await CreateAndCloneAsync(("report.txt", "base"));
        _a.WriteFile("report.txt", "a's edit");
        (await RunAsync(_a, "sync")).ExitCode.ShouldBe(0);
        _b.WriteFile("report.txt", "b's edit");

        CliResult text = await RunAsync(_b, "sync");
        CliResult json = await RunAsync(_b, "sync", "--json");

        text.ExitCode.ShouldBe(3, text.ToString());
        text.StdOut.ShouldContain("1 conflict");
        text.StdOut.ShouldContain("report.txt");
        text.StdOut.ShouldContain("modify/modify");
        json.ExitCode.ShouldBe(3);
        using var document = JsonDocument.Parse(json.StdOut);
        JsonElement conflict = document.RootElement.GetProperty("conflicts").EnumerateArray().Single();
        conflict.GetProperty("path").GetString().ShouldBe("report.txt");
        conflict.GetProperty("kind").GetString().ShouldBe("modify/modify");
        document.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(3);
        _b.ReadText("report.txt").ShouldBe("b's edit");
    }

    [Fact]
    public async Task ASkippedPath_Should_ExitNine_AndSayWhy()
    {
        _a.WriteFile("real.txt", "r");
        try
        {
            File.CreateSymbolicLink(_a.Combine("link.txt"), _a.Combine("real.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }

        CliResult created = await RunAsync(_a, "sync");

        created.ExitCode.ShouldBe(9, created.ToString());
        created.StdOut.ShouldContain("1 path skipped");
        created.StdOut.ShouldContain("link.txt");
        created.StdOut.ShouldContain("symlink");
    }

    [Fact]
    public async Task FolderPreconditions_Should_ExitFour()
    {
        string shareId = await CreateAndCloneAsync(("a.txt", "a"));
        TempShareFolder notEmpty = new();
        try
        {
            notEmpty.WriteFile("mine.txt", "mine");

            CliResult intoNonEmpty = await RunAsync(notEmpty, "sync", shareId);
            CliResult intoAShare = await RunAsync(_b, "sync", shareId);

            intoNonEmpty.ExitCode.ShouldBe(4, intoNonEmpty.ToString());
            intoAShare.ExitCode.ShouldBe(4, intoAShare.ToString());
            notEmpty.Exists(StateFileNames.State).ShouldBeFalse();
        }
        finally
        {
            notEmpty.Dispose();
        }
    }

    [Fact]
    public async Task CloningAShareThatDoesNotExist_Should_ExitEight()
    {
        CliResult result = await RunAsync(_b, "sync", Guid.NewGuid().ToString());

        result.ExitCode.ShouldBe(8, result.ToString());
        result.StdErr.ShouldContain("not found", Case.Insensitive);
    }

    [Fact]
    public async Task MissingConfiguration_Should_ExitTwo_NamingTheVariable_AlsoInJson()
    {
        _a.WriteFile("a.txt", "a");

        CliResult text = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>(), "sync");
        CliResult json = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>(), "sync", "--json");

        text.ExitCode.ShouldBe(2, text.ToString());
        text.StdErr.ShouldContain("AZURE_STORAGE_CONNECTION_STRING");
        json.ExitCode.ShouldBe(2);
        using var document = JsonDocument.Parse(json.StdOut);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("Configuration.Missing");
        _a.Exists(StateFileNames.State).ShouldBeFalse();
    }

    [Theory]
    [InlineData("MYSYNC_PROVIDER", "s3-someday")]
    [InlineData("MYSYNC_PARALLELISM", "lots")]
    [InlineData("MYSYNC_PARALLELISM", "65")]
    [InlineData("MYSYNC_CONTAINER", "Not_Valid")]
    public async Task AnInvalidSetting_Should_ExitTwo_NamingTheVariable(string variable, string value)
    {
        _a.WriteFile("a.txt", "a");

        CliResult result = await RunAsync(_a, new Dictionary<string, string?> { [variable] = value }, "sync");

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldContain(variable);
    }

    [Fact]
    public async Task AWrongKey_Should_ExitEight_AndNoOutputShouldCarryTheKey_EvenVerbose()
    {
        _a.WriteFile("a.txt", "a");
        string wrongKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        string connectionString = string.Join(';', _azurite.ConnectionString.Split(';')
            .Select(part => part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase) ? $"AccountKey={wrongKey}" : part));

        CliResult result = await RunAsync(_a, new Dictionary<string, string?> { ["AZURE_STORAGE_CONNECTION_STRING"] = connectionString }, "sync", "--verbose");

        result.ExitCode.ShouldBe(8, result.ToString());
        result.StdErr.ShouldContain("rejected the credentials");
        (result.StdOut + result.StdErr).ShouldNotContain(wrongKey);
        (result.StdOut + result.StdErr).ShouldNotContain(wrongKey[..20]);
    }

    [Fact]
    public async Task DeletingMostFiles_Should_ExitSeven_UntilAllowed()
    {
        (string Path, string Content)[] files = [.. Enumerable.Range(0, 12).Select(i => ($"f{i:D2}.txt", $"{i}"))];
        await CreateAndCloneAsync(files);
        foreach ((string path, _) in files)
        {
            File.Delete(_a.Combine(path));
        }

        CliResult blocked = await RunAsync(_a, "sync");
        CliResult allowed = await RunAsync(_a, "sync", "--allow-mass-delete");

        blocked.ExitCode.ShouldBe(7, blocked.ToString());
        blocked.StdErr.ShouldContain("--allow-mass-delete");
        allowed.ExitCode.ShouldBe(0, allowed.ToString());
    }

    [Fact]
    public async Task DryRun_Should_ShowThePlan_AndChangeNothing()
    {
        await CreateAndCloneAsync(("a.txt", "a"));
        _a.WriteFile("a.txt", "edited").WriteFile("new.txt", "new");
        string stateBefore = _a.ReadText(StateFileNames.State);

        CliResult dryRun = await RunAsync(_a, "sync", "--dry-run");
        CliResult other = await RunAsync(_b, "sync");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        dryRun.StdOut.ShouldContain("Dry run");
        dryRun.StdOut.ShouldContain("↑ upload (2)");
        dryRun.StdOut.ShouldContain("new.txt");
        _a.ReadText(StateFileNames.State).ShouldBe(stateBefore);
        other.StdOut.ShouldContain("Already up to date");
    }

    [Fact]
    public async Task ALargeFolder_Should_NeedYes_WhenThereIsNoTerminal_AndUploadNothingWithout()
    {
        foreach (int i in Enumerable.Range(0, SyncThreshold + 1))
        {
            _a.WriteFile($"many/{i:D4}.txt", "");
        }

        CliResult refused = await RunAsync(_a, "sync");

        refused.ExitCode.ShouldBe(2, refused.ToString());
        refused.StdErr.ShouldContain("--yes");
        _a.Exists(StateFileNames.State).ShouldBeFalse();

        CliResult confirmed = await RunAsync(_a, "sync", "--yes");

        confirmed.ExitCode.ShouldBe(0, confirmed.ToString());
        confirmed.StdOut.ShouldContain($"({SyncThreshold + 1} files");
    }

    [Fact]
    public async Task AFolderInUse_Should_ExitSix()
    {
        await CreateAndCloneAsync(("a.txt", "a"));
        await using FileStream held = new(_a.Combine(StateFileNames.Lock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        CliResult result = await RunAsync(_a, "sync");

        result.ExitCode.ShouldBe(6, result.ToString());
        result.StdErr.ShouldContain("Another mysync process");
    }

    [Fact]
    public async Task ADryRunOfANewShare_Should_PreviewIt_AndUploadNothing()
    {
        _a.WriteFile("a.txt", "12345");

        CliResult result = await RunAsync(_a, "sync", "--dry-run");

        result.ExitCode.ShouldBe(0, result.ToString());
        result.StdOut.ShouldContain("would create one from 1 file (5 B)");
        _a.Exists(StateFileNames.State).ShouldBeFalse();
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private const int SyncThreshold = 1000;

    private async Task<string> CreateAndCloneAsync(params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            _a.WriteFile(path, content);
        }

        (await RunAsync(_a, "sync")).ExitCode.ShouldBe(0);
        string shareId = ShareIdOf(_a);
        (await RunAsync(_b, "sync", shareId)).ExitCode.ShouldBe(0);
        return shareId;
    }

    private Task<CliResult> RunAsync(TempShareFolder folder, params string[] arguments) =>
        RunAsync(folder, new Dictionary<string, string?>(), arguments);

    private Task<CliResult> RunAsync(TempShareFolder folder, Dictionary<string, string?> overrides, params string[] arguments)
    {
        Dictionary<string, string?> environment = new()
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = _azurite.ConnectionString,
            ["MYSYNC_CONTAINER"] = _container,
        };
        foreach ((string name, string? value) in overrides)
        {
            environment[name] = value;
        }

        return CliProcess.RunAsync(folder.Path, environment, arguments);
    }

    private static string ShareIdOf(TempShareFolder folder)
    {
        using var state = JsonDocument.Parse(folder.ReadText(StateFileNames.State));
        return state.RootElement.GetProperty("shareId").GetString()!;
    }
}
