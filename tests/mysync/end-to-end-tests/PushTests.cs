using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync.Push;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.AzureMachine;

namespace MySync.EndToEndTests;

/// <summary>
/// <c>mysync push</c> on Azurite with the full DI graph and real folders (CLAUDE.md §10b): servers
/// push into a laptop's share at the same time, the laptop's next sync gets everything, and what it
/// then does with the pushed folders is ordinary syncing.
/// </summary>
public sealed class PushTests : IDisposable
{
    private readonly AzureMachine _laptop;
    private readonly AzureMachine _web01;
    private readonly AzureMachine _web02;
    private readonly string _container = AzuriteEnvironment.NewContainerName();

    public PushTests(AzuriteFixture azurite)
    {
        _laptop = new AzureMachine(azurite.ConnectionString, _container);
        _web01 = new AzureMachine(azurite.ConnectionString, _container);
        _web02 = new AzureMachine(azurite.ConnectionString, _container);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PushCommand Push(string share, string host, params string[] paths) => new(ShareReference.Parse(share).Value, paths, Host: host);

    [Fact]
    public async Task UpdatedSinceLast_Should_AddAFolderWithOnlyTheChangedFiles_AndTheLaptopGetsBoth()
    {
        _laptop.Folder.WriteFile("notes.md", "# logs\n");
        Ok(await _laptop.CreateNamedAsync("app-logs"));
        _web01.Folder.WriteFile("app/server.log", "line 1\n").WriteFile("app/access.log", "GET /\n").WriteFile("app/old/rotated.log", "old\n");
        DateTime anHourAgo = DateTime.UtcNow.AddHours(-1);
        foreach (string path in new[] { "app/server.log", "app/access.log", "app/old/rotated.log" })
        {
            File.SetLastWriteTimeUtc(_web01.Folder.Combine(path), anHourAgo);
        }

        PushReport first = Ok(await _web01.PushAsync(Push("app-logs", "web01") with { UpdatedSince = "last" }));
        await File.AppendAllTextAsync(_web01.Folder.Combine("app/server.log"), "line 2\n", Ct);
        _web01.Folder.WriteFile("app/new.log", "new\n");
        PushReport second = Ok(await _web01.PushAsync(Push("app-logs", "web01") with { UpdatedSince = "last" }));

        first.Files.ShouldBe(3);
        first.Filters!.UpdatedSinceUtc.ShouldBeNull();
        second.Target.ShouldNotBe(first.Target);
        second.Files.ShouldBe(2);
        second.FilteredOut.NotChanged.ShouldBe(2);
        second.Filters!.UpdatedSinceUtc!.Value.UtcDateTime.ToString("yyyyMMdd-HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(first.Target["web01/".Length..]);

        SyncReport synced = Ok(await _laptop.SyncAsync());

        synced.Skipped.ShouldBeEmpty();
        _laptop.Folder.ReadText($"{first.Target}/app/server.log").ShouldBe("line 1\n");
        _laptop.Folder.ReadText($"{first.Target}/app/old/rotated.log").ShouldBe("old\n");
        _laptop.Folder.ReadText($"{second.Target}/app/server.log").ShouldBe("line 1\nline 2\n");
        _laptop.Folder.ReadText($"{second.Target}/app/new.log").ShouldBe("new\n");
        File.Exists(_laptop.Folder.Combine($"{second.Target}/app/access.log")).ShouldBeFalse();
        Directory.Exists(_laptop.Folder.Combine($"{second.Target}/app/old")).ShouldBeFalse();
        _laptop.Folder.ReadText($"{second.Target}/{PushRecord.FileName}").ShouldContain("\"updatedSinceSpec\": \"last\"");
    }

    [Fact]
    public async Task IncludeAndMaxSize_Should_PushTheExpectedSubset()
    {
        _laptop.Folder.WriteFile("notes.md", "# logs\n");
        Ok(await _laptop.CreateNamedAsync("app-logs-subset"));
        _web01.Folder
            .WriteFile("app/a.log", "a\n")
            .WriteFile("app/huge.log", RandomBytes(1024 * 1024 + 1))
            .WriteFile("app/limit.log", RandomBytes(1024 * 1024))
            .WriteFile("app/data.bin", "bin")
            .CreateDirectory("app/empty");

        PushReport report = Ok(await _web01.PushAsync(Push("app-logs-subset", "web01") with { Include = ["*.log"], MaxSize = "1MB" }));

        report.Files.ShouldBe(2);
        report.FilteredOut.ShouldBe(new PushFilteredOut(NotChanged: 0, NotIncluded: 1, Excluded: 0, TooLarge: 1));
        Ok(await _laptop.SyncAsync());
        string[] pushed = [.. Directory.EnumerateFileSystemEntries(_laptop.Folder.Combine(report.Target), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_laptop.Folder.Combine(report.Target), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
        pushed.ShouldBe([PushRecord.FileName, "app", "app/a.log", "app/limit.log"]);
    }

    [Fact]
    public async Task TwoServersPushingAtOnce_Should_BothReachTheLaptop_WhichThenSyncsThemNormally()
    {
        _laptop.Folder.WriteFile("notes.md", "# incident\n");
        ShareId shareId = Ok(await _laptop.CreateNamedAsync("incident-logs")).ShareId;
        _web01.Folder.WriteFile("app/error.log", "web01 error\n").WriteFile("app/big.bin", RandomBytes(5 * 1024 * 1024)).CreateDirectory("app/empty");
        _web02.Folder.WriteFile("app/error.log", "web02 error\n").WriteFile("config.yml", "port: 80\n");

        PushReport[] pushed = await Task.WhenAll(
            Task.Run(async () => Ok(await _web01.PushAsync(Push("incident-logs", "web01")))),
            Task.Run(async () => Ok(await _web02.PushAsync(Push("incident-logs", "web02")))));

        pushed.Select(report => report.Revision!.Value).Order().ShouldBe([2L, 3L]);
        string web01 = pushed[0].Target;
        string web02 = pushed[1].Target;
        web01.ShouldStartWith("web01/");
        web02.ShouldStartWith("web02/");

        SyncReport synced = Ok(await _laptop.SyncAsync());

        synced.Conflicts.ShouldBeEmpty();
        synced.Skipped.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(_laptop.Folder.Combine($"{web01}/app/big.bin"), Ct)).ShouldBe(await File.ReadAllBytesAsync(_web01.Folder.Combine("app/big.bin"), Ct));
        _laptop.Folder.ReadText($"{web01}/app/error.log").ShouldBe("web01 error\n");
        Directory.Exists(_laptop.Folder.Combine($"{web01}/app/empty")).ShouldBeTrue();
        _laptop.Folder.ReadText($"{web02}/app/error.log").ShouldBe("web02 error\n");
        _laptop.Folder.ReadText($"{web02}/config.yml").ShouldBe("port: 80\n");
        _laptop.Folder.ReadText($"{web02}/{Domain.Sync.Push.PushRecord.FileName}").ShouldContain("\"host\": \"web02\"");
        _laptop.Folder.ReadText("notes.md").ShouldBe("# incident\n");

        // The servers kept no state of their own.
        File.Exists(_web01.Folder.Combine(StateFileNames.State)).ShouldBeFalse();
        File.Exists(_web01.Folder.Combine(StateFileNames.Lock)).ShouldBeFalse();
        Directory.Exists(_web01.Folder.Combine(StateFileNames.StagingDirectory)).ShouldBeFalse();

        // Deleting one pushed folder in the clone is an ordinary sync.
        Directory.Delete(_laptop.Folder.Combine(web02), recursive: true);
        Directory.Delete(_laptop.Folder.Combine("web02"));
        SyncReport removed = Ok(await _laptop.SyncAsync());

        removed.DeletedRemotely.ShouldBe(3);
        Manifest head = await HeadAsync(shareId);
        head.Entries.Keys.ShouldNotContain(path => path.Value.StartsWith("web02/", StringComparison.Ordinal));
        head.Entries.Keys.ShouldContain(path => path.Value == $"{web01}/app/error.log");
    }

    [Fact]
    public async Task CreateFromAServer_Should_MakeAShareTheLaptopCanClone()
    {
        _web01.Folder.WriteFile("logs/a.log", "a\n");

        PushReport report = Ok(await _web01.PushAsync(new PushCommand(ShareReference.Parse("collected").Value, ["logs"], Host: "web01", Create: true)));

        report.Created.ShouldBeTrue();
        Ok(await _laptop.CloneAsync("collected"));
        _laptop.Folder.ReadText($"{report.Target}/logs/a.log").ShouldBe("a\n");
    }

    [Fact]
    public async Task AFolderThatSyncsWithAnotherShare_Should_PushNormally_ItsStateUntouched()
    {
        _laptop.Folder.WriteFile("own.txt", "laptop's own share");
        ShareId other = await _laptop.CreateAsync();
        _web01.Folder.WriteFile("collect.txt", "x");
        ShareId target = Ok(await _web01.CreateNamedAsync("target-share")).ShareId;
        byte[] laptopState = await File.ReadAllBytesAsync(_laptop.Folder.Combine(StateFileNames.State), Ct);

        PushReport report = Ok(await _laptop.PushAsync(Push("target-share", "laptop")));

        report.Files.ShouldBe(1);
        (await File.ReadAllBytesAsync(_laptop.Folder.Combine(StateFileNames.State), Ct)).ShouldBe(laptopState);
        Manifest head = await HeadAsync(target);
        head.Entries.Keys.ShouldNotContain(path => path.Value.Contains(".mysynctool", StringComparison.Ordinal));
        head.Entries.Keys.ShouldContain(path => path.Value == $"{report.Target}/own.txt");
        (await HeadAsync(other)).Revision.Value.ShouldBe(1);
    }

    [Fact]
    public async Task AFolderOfTheTargetShare_Should_BeRefused_EvenFromASubfolder()
    {
        _web01.Folder.WriteFile("logs/a.log", "a");
        Ok(await _web01.CreateNamedAsync("own-share"));

        SharedKernel.Error error = (await _web01.PushAsync(Push("own-share", "web01", "logs"))).Error;

        error.Code.ShouldBe("Push.SourceIsTheShare");
        error.Description.ShouldContain("own-share");
    }

    private Task<Manifest> HeadAsync(ShareId shareId) =>
        _laptop.WithStoreAsync(async store => (await store.GetManifestAsync(shareId, Ct)).Value!.Manifest);

    public void Dispose()
    {
        _laptop.Dispose();
        _web01.Dispose();
        _web02.Dispose();
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
