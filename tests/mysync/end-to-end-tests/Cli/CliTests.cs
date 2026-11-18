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

    [Fact]
    public async Task StatusConflictsAndInfo_Should_ShowTheConflict_WithoutChangingAnything()
    {
        await ConflictOnBAsync();
        string stateBefore = _b.ReadText(StateFileNames.State);

        CliResult status = await RunAsync(_b, "status");
        CliResult conflicts = await RunAsync(_b, "conflicts", "--json");
        CliResult info = await RunAsync(_b, "info", "--json");

        status.ExitCode.ShouldBe(3, status.ToString());
        status.StdOut.ShouldContain("Status");
        status.StdOut.ShouldContain("report.txt");
        conflicts.ExitCode.ShouldBe(0, conflicts.ToString());
        using var listed = JsonDocument.Parse(conflicts.StdOut);
        listed.RootElement.GetProperty("conflicts").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("report.txt");
        info.ExitCode.ShouldBe(0, info.ToString());
        using var described = JsonDocument.Parse(info.StdOut);
        described.RootElement.GetProperty("openConflicts").GetInt32().ShouldBe(1);
        described.RootElement.GetProperty("accountName").GetString().ShouldBe(_azurite.AccountName);
        described.RootElement.GetProperty("remoteRevision").GetInt64().ShouldBe(2);
        _b.ReadText(StateFileNames.State).ShouldBe(stateBefore);
    }

    [Fact]
    public async Task ResolveKeepLocal_Should_ExitZero_AndTheOtherMachineGetsThatVersion()
    {
        await ConflictOnBAsync();

        CliResult resolved = await RunAsync(_b, "resolve", "./report.txt", "--keep", "local");
        CliResult pulled = await RunAsync(_a, "sync");
        CliResult conflicts = await RunAsync(_b, "conflicts");

        resolved.ExitCode.ShouldBe(0, resolved.ToString());
        resolved.StdOut.ShouldContain("Resolved 1 conflict");
        pulled.ExitCode.ShouldBe(0, pulled.ToString());
        _a.ReadText("report.txt").ShouldBe("b's edit");
        conflicts.StdOut.ShouldContain("No open conflicts");
        _b.ReadText(StateFileNames.ConflictLog).ShouldContain("RESOLVED keep-local report.txt");
    }

    [Fact]
    public async Task ResolveAllKeepRemote_Should_TakeTheSharesVersion_AsJson()
    {
        await ConflictOnBAsync();

        CliResult resolved = await RunAsync(_b, "resolve", "--all", "--keep", "remote", "--json");

        resolved.ExitCode.ShouldBe(0, resolved.ToString());
        using var json = JsonDocument.Parse(resolved.StdOut);
        json.RootElement.GetProperty("command").GetString().ShouldBe("resolve");
        json.RootElement.GetProperty("resolved").EnumerateArray().Select(path => path.GetString()).ShouldBe(["report.txt"]);
        _b.ReadText("report.txt").ShouldBe("a's edit");
    }

    [Theory]
    [InlineData("resolve", "report.txt")]                                // no --keep
    [InlineData("resolve", "--keep", "both", "report.txt")]
    [InlineData("resolve", "--keep", "local")]                           // no path, no --all
    [InlineData("resolve", "notes.txt", "--keep", "local")]              // not in conflict
    [InlineData("gc", "--keep-revisions", "0")]
    [InlineData("gc", "--grace-hours", "0")]
    public async Task BadResolveAndGcArguments_Should_ExitTwo(params string[] arguments)
    {
        await ConflictOnBAsync();

        CliResult result = await RunAsync(_b, arguments);

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Gc_Should_ListWhatItWouldDelete_AndDeleteNothingOnADryRun()
    {
        await CreateAndCloneAsync(("a.txt", "a"));

        CliResult dryRun = await RunAsync(_a, "gc", "--dry-run", "--json");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        using var json = JsonDocument.Parse(dryRun.StdOut);
        json.RootElement.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("deletedObjects").GetArrayLength().ShouldBe(0);
        json.RootElement.GetProperty("keptObjects").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("info")]
    [InlineData("conflicts")]
    [InlineData("gc")]
    public async Task ShareCommands_Should_ExitFour_InAFolderThatIsNotAShare(string command)
    {
        CliResult result = await RunAsync(_a, command);

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("not a share");
    }

    [Fact]
    public async Task Delete_Should_RefuseWithoutYes_AndChangeNothing()
    {
        string shareId = await CreateAndCloneAsync(("a.txt", "a"));

        CliResult refused = await RunAsync(_a, "delete");

        refused.ExitCode.ShouldBe(2, refused.ToString());
        refused.StdErr.ShouldContain("--yes");
        _a.Exists(StateFileNames.State).ShouldBeTrue();
        (await RunAsync(_b, "sync")).ExitCode.ShouldBe(0);
        ShareIdOf(_b).ShouldBe(shareId);
    }

    [Fact]
    public async Task Delete_Should_RemoveTheShare_AndOtherMachinesExitEight_SayingItWasDeleted()
    {
        await CreateAndCloneAsync(("secret.txt", "s3cr3t"));

        CliResult deleted = await RunAsync(_a, "delete", "--yes");
        CliResult synced = await RunAsync(_b, "sync");
        CliResult json = await RunAsync(_b, "status", "--json");

        deleted.ExitCode.ShouldBe(0, deleted.ToString());
        deleted.StdOut.ShouldContain("Deleted share");
        deleted.StdOut.ShouldContain("ordinary folder again");
        _a.Exists(StateFileNames.State).ShouldBeFalse();
        _a.ReadText("secret.txt").ShouldBe("s3cr3t");
        synced.ExitCode.ShouldBe(8, synced.ToString());
        synced.StdErr.ShouldContain("was deleted by");
        _b.ReadText("secret.txt").ShouldBe("s3cr3t");
        using var document = JsonDocument.Parse(json.StdOut);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("Shares.Deleted");
        document.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(8);
    }

    [Fact]
    public async Task DeleteById_Should_Work_FromAnyFolder_AsJson()
    {
        string shareId = await CreateAndCloneAsync(("a.txt", "a"));
        TempShareFolder elsewhere = new();
        try
        {
            CliResult deleted = await RunAsync(elsewhere, "delete", shareId, "--yes", "--json");

            deleted.ExitCode.ShouldBe(0, deleted.ToString());
            using var json = JsonDocument.Parse(deleted.StdOut);
            json.RootElement.GetProperty("command").GetString().ShouldBe("delete");
            json.RootElement.GetProperty("shareId").GetString().ShouldBe(shareId);
            json.RootElement.GetProperty("objectsDeleted").GetInt32().ShouldBe(1);
            json.RootElement.GetProperty("localStateRemoved").GetBoolean().ShouldBeFalse();
            (await RunAsync(_b, "info")).ExitCode.ShouldBe(8);
        }
        finally
        {
            elsewhere.Dispose();
        }
    }

    [Fact]
    public async Task UpdateCheck_Should_ReportANewerRelease_FromALocalReleaseFolder_AsJson()
    {
        _a.WriteFile("releases/mysync-99.0.0/SHA256SUMS.txt", "unused for a check").WriteFile("releases/mysync-99.1.0-beta.1/SHA256SUMS.txt", "prerelease");

        CliResult text = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_UPDATE_SOURCE"] = _a.Combine("releases") }, "update", "--check");
        CliResult json = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_UPDATE_SOURCE"] = _a.Combine("releases") }, "update", "--check", "--json");

        text.ExitCode.ShouldBe(0, text.ToString());
        text.StdOut.ShouldContain("mysync 99.0.0 is available");
        text.StdErr.ShouldContain("significant change");
        using var document = JsonDocument.Parse(json.StdOut);
        document.RootElement.GetProperty("status").GetString().ShouldBe("available");
        document.RootElement.GetProperty("targetVersion").GetString().ShouldBe("99.0.0");
    }

    [Fact]
    public async Task Update_Should_RefuseToReplaceADevelopmentBuild_ExitFour()
    {
        _a.WriteFile("releases/mysync-99.0.0/SHA256SUMS.txt", "x");

        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_UPDATE_SOURCE"] = _a.Combine("releases") }, "update");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldContain("can't update itself");
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // a and b both edit report.txt; a syncs first, so b holds the conflict. notes.txt is not in conflict.
    private async Task ConflictOnBAsync()
    {
        await CreateAndCloneAsync(("report.txt", "base"), ("notes.txt", "n"));
        _a.WriteFile("report.txt", "a's edit");
        (await RunAsync(_a, "sync")).ExitCode.ShouldBe(0);
        _b.WriteFile("report.txt", "b's edit");
        (await RunAsync(_b, "sync")).ExitCode.ShouldBe(3);
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

    [Fact]
    public async Task PublishPublicationsAndUnpublish_Should_Work_AndAnUnverifiedPublicationExitTen()
    {
        Assert.SkipWhen(_azurite.PublishConnectionString is null, "No publishing account on supplied storage.");
        _a.WriteFile("docs/readme.md", "# hi");
        (await RunAsync(_a, "sync")).ExitCode.ShouldBe(0);
        Dictionary<string, string?> publishing = new()
        {
            ["MYSYNC_PUBLISH_CONNECTION_STRING"] = _azurite.PublishConnectionString,
            ["MYSYNC_PUBLISH_BASE_URL"] = "https://mysyncpublish.invalid", // nothing answers there: verification fails
        };

        CliResult published = await RunAsync(_a, publishing, "publish", "--title", "Q3 reports", "--json");

        published.ExitCode.ShouldBe(10, published.ToString());
        using var publishJson = JsonDocument.Parse(published.StdOut);
        publishJson.RootElement.GetProperty("verified").GetBoolean().ShouldBeFalse();
        publishJson.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
        publishJson.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(10);
        string publishId = publishJson.RootElement.GetProperty("publishId").GetString()!;
        publishJson.RootElement.GetProperty("url").GetString().ShouldBe($"https://mysyncpublish.invalid/p/{publishId}/");
        publishJson.RootElement.GetProperty("shellCommand").GetString().ShouldBe($"curl -fsSL https://mysyncpublish.invalid/p/{publishId}/download.sh | sh");

        CliResult listed = await RunAsync(_a, "publications", "--json");
        listed.ExitCode.ShouldBe(0, listed.ToString());
        using var listJson = JsonDocument.Parse(listed.StdOut);
        JsonElement record = listJson.RootElement.GetProperty("publications").EnumerateArray().Single();
        record.GetProperty("id").GetString().ShouldBe(publishId);
        record.GetProperty("status").GetString().ShouldBe("published");
        record.GetProperty("title").GetString().ShouldBe("Q3 reports");

        CliResult human = await RunAsync(_a, publishing, "publish");
        human.ExitCode.ShouldBe(10, human.ToString());
        human.StdOut.ShouldContain("Published");
        human.StdErr.ShouldContain("couldn't be opened anonymously");
        human.StdErr.ShouldContain("MYSYNC_PUBLISH_BASE_URL");

        CliResult removed = await RunAsync(_a, publishing, "unpublish", publishId);
        removed.ExitCode.ShouldBe(0, removed.ToString());
        removed.StdOut.ShouldContain($"Unpublished {publishId}");

        CliResult gone = await RunAsync(_a, publishing, "unpublish", publishId);
        gone.ExitCode.ShouldBe(8, gone.ToString());

        foreach (CliResult result in (CliResult[])[published, listed, human, removed, gone])
        {
            (result.StdOut + result.StdErr).ShouldNotContain(AzuriteEnvironment.AzuriteKey);
        }
    }

    [Fact]
    public async Task PublishingWithoutItsSettings_Should_ExitTwo_NamingTheVariable_AndDeleteShouldRefuse()
    {
        Assert.SkipWhen(_azurite.PublishConnectionString is null, "No publishing account on supplied storage.");
        _a.WriteFile("a.txt", "a");
        (await RunAsync(_a, "sync")).ExitCode.ShouldBe(0);

        CliResult noBaseUrl = await RunAsync(_a, new Dictionary<string, string?> { ["MYSYNC_PUBLISH_CONNECTION_STRING"] = _azurite.PublishConnectionString }, "publish");
        noBaseUrl.ExitCode.ShouldBe(2, noBaseUrl.ToString());
        noBaseUrl.StdErr.ShouldContain("MYSYNC_PUBLISH_BASE_URL");

        CliResult published = await RunAsync(_a, new Dictionary<string, string?>
        {
            ["MYSYNC_PUBLISH_CONNECTION_STRING"] = _azurite.PublishConnectionString,
            ["MYSYNC_PUBLISH_BASE_URL"] = "https://mysyncpublish.invalid",
        }, "publish");
        published.ExitCode.ShouldBe(10, published.ToString());

        CliResult delete = await RunAsync(_a, "delete", "--yes");
        delete.ExitCode.ShouldBe(2, delete.ToString());
        delete.StdErr.ShouldContain("1 publication(s)");
        File.Exists(Path.Combine(_a.Path, StateFileNames.State)).ShouldBeTrue("nothing was deleted");
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
