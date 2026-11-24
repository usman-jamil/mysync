using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Sync;

/// <summary>
/// The step-5 pipeline with the real local machine underneath: real folders, scanner, state
/// file, lock, ignore rules and conflict log; only storage is the in-memory store. Two temp
/// folders play two machines.
/// </summary>
public sealed class DiskSyncTests : IDisposable
{
    private static readonly DateTime OldMtime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryAliasStore _aliases = new();
    private readonly InMemoryShareStore _store = new();
    private readonly TempShareFolder _a = new();
    private readonly TempShareFolder _b = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateThenClone_Should_ReproduceTheFolder_WithExecBitsMtimesEmptyDirsAndNfcNames()
    {
        _a.WriteFile("docs/readme.md", "# hi", OldMtime)
            .WriteFile("scripts/deploy.sh", "#!/bin/sh\necho hi\n", OldMtime, executable: true)
            .WriteFile("café.txt", "coffee", OldMtime)
            .WriteFile(".mysyncignore", "*.log\n", OldMtime)
            .WriteFile("debug.log", "noise", OldMtime)
            .WriteFile(".DS_Store", "finder", OldMtime)
            .CreateDirectory("logs");

        CreateShareResponse created = (await Send<CreateShareCommand, CreateShareResponse>(_a, new CreateShareCommand(MySync.Domain.Shares.ShareName.Create("disk-" + Guid.NewGuid().ToString("N")[..12]).Value))).Value;
        SyncReport cloned = Ok(await Send<CloneShareCommand, SyncReport>(_b, new CloneShareCommand(MySync.Domain.Shares.ShareReference.For(created.ShareId))));

        cloned.Downloaded.Files.ShouldBe(4);
        _b.Files().ShouldBe(new Dictionary<string, string>
        {
            [".mysyncignore"] = "*.log\n",
            ["café.txt"] = "coffee",
            ["docs/readme.md"] = "# hi",
            ["scripts/deploy.sh"] = "#!/bin/sh\necho hi\n",
        });
        Directory.Exists(_b.Combine("logs")).ShouldBeTrue();
        _b.IsExecutable("scripts/deploy.sh").ShouldBe(!OperatingSystem.IsWindows());
        File.GetLastWriteTimeUtc(_b.Combine("docs/readme.md")).ShouldBe(OldMtime);
        _b.Exists(StateFileNames.State).ShouldBeTrue();
        Directory.Exists(_b.Combine(StateFileNames.StagingDirectory)).ShouldBeFalse();
    }

    [Fact]
    public async Task AGitignore_Should_BeAppliedWithTheMysyncignore_AndBothAreSynced()
    {
        _a.WriteFile("src/app.cs", "class App;", OldMtime)
            .WriteFile(".mysyncignore", "*.log\n", OldMtime)
            .WriteFile(".gitignore", "bin/\n!keep.log\n", OldMtime)
            .WriteFile("bin/app.dll", "binary", OldMtime)
            .WriteFile("debug.log", "noise", OldMtime)
            .WriteFile("keep.log", "still ignored by .mysyncignore", OldMtime);

        CreateShareResponse created = (await Send<CreateShareCommand, CreateShareResponse>(_a, new CreateShareCommand(MySync.Domain.Shares.ShareName.Create("disk-" + Guid.NewGuid().ToString("N")[..12]).Value))).Value;
        Ok(await Send<CloneShareCommand, SyncReport>(_b, new CloneShareCommand(MySync.Domain.Shares.ShareReference.For(created.ShareId))));

        _b.Files().ShouldBe(new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\n!keep.log\n",
            [".mysyncignore"] = "*.log\n",
            ["src/app.cs"] = "class App;",
        });

        // On the clone, the .gitignore it downloaded applies too: its own build output stays local.
        _b.WriteFile("bin/b.dll", "b's build");
        SyncReport synced = Ok(await SyncAsync(_b));
        synced.Uploaded.Files.ShouldBe(0);
        Ok(await SyncAsync(_a));
        _a.Exists("bin/b.dll").ShouldBeFalse();
    }

    [Fact]
    public async Task TwoWaySync_Should_LeaveBothFoldersIdentical()
    {
        ShareId shareId = await CreateAndCloneAsync(("keep.txt", "k"), ("edit-on-a.txt", "1"), ("edit-on-b.txt", "1"), ("delete-on-b.txt", "x"), ("folder/inside.txt", "i"));
        _a.WriteFile("edit-on-a.txt", "a's edit").WriteFile("new-on-a/deep.txt", "new");
        _b.WriteFile("edit-on-b.txt", "b's edit");
        File.Delete(_b.Combine("delete-on-b.txt"));
        Directory.Delete(_b.Combine("folder"), recursive: true);

        Ok(await SyncAsync(_a));
        Ok(await SyncAsync(_b));
        SyncReport last = Ok(await SyncAsync(_a));

        last.Downloaded.Files.ShouldBe(1);
        _a.Files().ShouldBe(_b.Files());
        _a.Files().Keys.ShouldBe(["edit-on-a.txt", "edit-on-b.txt", "keep.txt", "new-on-a/deep.txt"], ignoreOrder: true);
        Directory.Exists(_a.Combine("folder")).ShouldBeFalse();
        (await _store.GetManifestAsync(shareId, Ct)).Value!.Manifest.Revision.Value.ShouldBe(3);
    }

    [Fact]
    public async Task ASecondSyncWithNoChanges_Should_DoNothing()
    {
        await CreateAndCloneAsync(("a.txt", "a"), ("b/c.txt", "c"));

        SyncReport report = Ok(await SyncAsync(_a));

        report.Committed.ShouldBeFalse();
        report.Uploaded.Files.ShouldBe(0);
        report.Downloaded.Files.ShouldBe(0);
        report.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task AConflict_Should_LeaveTheLocalFileAlone_AndBeWrittenToTheConflictLogOnce()
    {
        await CreateAndCloneAsync(("report.txt", "base"));
        _a.WriteFile("report.txt", "a's edit");
        Ok(await SyncAsync(_a));
        _b.WriteFile("report.txt", "b's edit");

        SyncReport report = Ok(await SyncAsync(_b));
        Ok(await SyncAsync(_b));

        report.Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);
        _b.ReadText("report.txt").ShouldBe("b's edit");
        string[] log = _b.ReadText(StateFileNames.ConflictLog).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        log.Single().ShouldContain("CONFLICT modify/modify report.txt");
    }

    [Fact]
    public async Task ALockedFile_Should_BeSkipped_AndNotDeletedFromTheShare()
    {
        ShareId shareId = await CreateAndCloneAsync(("busy.db", "v1"), ("other.txt", "o"));
        _a.WriteFile("other.txt", "changed").WriteFile("busy.db", "v2"); // changed, so the hash cache can't answer for it
        await using FileStream holder = new(_a.Combine("busy.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        SyncReport report = Ok(await SyncAsync(_a));

        report.Skipped.Single().Cause.ShouldBe(SkipCause.Unreadable);
        report.DeletedRemotely.ShouldBe(0);
        (await _store.GetManifestAsync(shareId, Ct)).Value!.Manifest.Entries.Keys.Select(path => path.Value).ShouldBe(["busy.db", "other.txt"]);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AnExecOnlyChange_Should_Sync_WithoutTransferringBytes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows can't see the bit");
        await CreateAndCloneAsync(("tool.sh", "#!/bin/sh"));
        File.SetUnixFileMode(_a.Combine("tool.sh"), File.GetUnixFileMode(_a.Combine("tool.sh")) | UnixFileMode.UserExecute);

        SyncReport up = Ok(await SyncAsync(_a));
        SyncReport down = Ok(await SyncAsync(_b));

        up.Uploaded.Files.ShouldBe(0);
        up.Committed.ShouldBeTrue();
        down.Downloaded.Files.ShouldBe(0);
        _b.IsExecutable("tool.sh").ShouldBeTrue();
    }

    [Fact]
    public async Task ADamagedStateFile_Should_StopTheSync_NotStartANewShare()
    {
        await CreateAndCloneAsync(("a.txt", "a"));
        await File.WriteAllTextAsync(_a.Combine(StateFileNames.State), "{ broken", Ct);

        (await SyncAsync(_a)).Error.Code.ShouldBe("State.Corrupt");
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private async Task<ShareId> CreateAndCloneAsync(params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            _a.WriteFile(path, content, OldMtime);
        }

        ShareId shareId = (await Send<CreateShareCommand, CreateShareResponse>(_a, new CreateShareCommand(MySync.Domain.Shares.ShareName.Create("disk-" + Guid.NewGuid().ToString("N")[..12]).Value))).Value.ShareId;
        Ok(await Send<CloneShareCommand, SyncReport>(_b, new CloneShareCommand(MySync.Domain.Shares.ShareReference.For(shareId))));
        return shareId;
    }

    private Task<Result<SyncReport>> SyncAsync(TempShareFolder folder) => Send<RunSyncCommand, SyncReport>(folder, new RunSyncCommand());

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(TempShareFolder folder, TCommand command)
        where TCommand : ICommand<TResponse>
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new ShareRoot(folder.Path));
        services.AddSingleton<IShareStore>(_store);
        services.AddSingleton<MySync.Application.Abstractions.Storage.IAliasStore>(_aliases);
        services.AddSingleton<TimeProvider>(new InstantTimeProvider());
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.ISiteAssetSource>(new InMemorySiteAssetSource());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.IPublishTarget>(provider =>
            new InMemoryPublishTarget((InMemoryShareStore)provider.GetRequiredService<MySync.Application.Abstractions.Storage.IShareStore>()));
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(provider => new MySync.Cli.Hosting.FolderCloneRunner(services, provider));
        services.AddApplication();
        services.AddInfrastructure();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, Ct);
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }
}
