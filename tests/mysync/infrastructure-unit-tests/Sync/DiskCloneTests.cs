using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.CloneInto;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Sync;

/// <summary>
/// <c>mysync clone</c> on a real disk (CLAUDE.md §9): the folder choice and its checks, the clone
/// run in that folder through the real composition (<c>FolderCloneRunner</c>), and the clean-up
/// after a failure or Ctrl+C. Only storage is in memory.
/// </summary>
public sealed class DiskCloneTests : IDisposable
{
    private static readonly DateTime OldMtime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryAliasStore _aliases = new();
    private readonly InMemoryShareStore _store = new();
    private readonly TempShareFolder _laptop = new();
    private readonly TempShareFolder _work = new(); // the current folder of the clone
    private readonly string _name = "app-" + Guid.NewGuid().ToString("N")[..12];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShareReference ByName => ShareReference.Parse(_name).Value;

    private async Task<ShareId> CreateShareAsync()
    {
        _laptop.WriteFile("docs/readme.md", "# readme", OldMtime)
            .WriteFile("scripts/deploy.sh", "#!/bin/sh\necho hi\n", OldMtime, executable: true)
            .WriteFile("z.txt", "last", OldMtime)
            .CreateDirectory("empty");
        return Ok(await Send<CreateShareCommand, CreateShareResponse>(_laptop, new CreateShareCommand(ShareName.Create(_name).Value))).ShareId;
    }

    private Task<Result<CloneIntoFolderResponse>> CloneAsync(CloneIntoFolderCommand command, CancellationToken? cancellationToken = null) =>
        Send<CloneIntoFolderCommand, CloneIntoFolderResponse>(_work, command, cancellationToken);

    // Phase 1: choosing and preparing the folder.

    [Fact]
    public async Task ByName_Should_CreateAFolderNamedAfterTheShare_AndCloneIntoIt()
    {
        ShareId shareId = await CreateShareAsync();

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName)));

        cloned.Folder.ShouldBe(_work.Combine(_name));
        cloned.CreatedFolder.ShouldBeTrue();
        cloned.Report.ShareId.ShouldBe(shareId);
        cloned.Report.Name.ShouldBe(ShareName.Create(_name).Value);
        cloned.Report.Downloaded.Files.ShouldBe(3);
        new TempShareFolderView(cloned.Folder).Files().ShouldBe(new Dictionary<string, string>
        {
            ["docs/readme.md"] = "# readme",
            ["scripts/deploy.sh"] = "#!/bin/sh\necho hi\n",
            ["z.txt"] = "last",
        });
        Directory.Exists(Path.Combine(cloned.Folder, "empty")).ShouldBeTrue();
        File.Exists(Path.Combine(cloned.Folder, StateFileNames.State)).ShouldBeTrue();
        File.Exists(_work.Combine(StateFileNames.State)).ShouldBeFalse(); // the current folder is untouched
        Directory.GetCurrentDirectory().ShouldNotBe(cloned.Folder);
    }

    [Fact]
    public async Task TheClonedFolder_Should_SyncBothWays_Afterwards()
    {
        await CreateShareAsync();
        string folder = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName))).Folder;
        TempShareFolderView clone = new(folder);

        await File.WriteAllTextAsync(Path.Combine(folder, "from-clone.txt"), "hello", Ct);
        Ok(await Send<RunSyncCommand, SyncReport>(folder, new RunSyncCommand()));
        _laptop.WriteFile("from-laptop.txt", "hi");
        Ok(await Send<RunSyncCommand, SyncReport>(_laptop.Path, new RunSyncCommand()));
        Ok(await Send<RunSyncCommand, SyncReport>(folder, new RunSyncCommand()));

        _laptop.ReadText("from-clone.txt").ShouldBe("hello");
        clone.Files()["from-laptop.txt"].ShouldBe("hi");
    }

    [Fact]
    public async Task ById_WithOneName_Should_UseThatName()
    {
        ShareId shareId = await CreateShareAsync();

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ShareReference.For(shareId))));

        cloned.Folder.ShouldBe(_work.Combine(_name));
        cloned.Report.Name.ShouldBe(ShareName.Create(_name).Value);
    }

    [Fact]
    public async Task ById_WithSeveralNames_Should_UseTheFirstInOrdinalOrder()
    {
        ShareId shareId = await CreateShareAsync();
        // An interrupted rename leaves two names pointing at the share.
        Ok(await _aliases.CreateAsync(new AliasRecord(ShareName.Create("aaa-" + _name).Value, shareId, DateTimeOffset.UtcNow, "test"), Ct));

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ShareReference.For(shareId))));

        cloned.Folder.ShouldBe(_work.Combine("aaa-" + _name));
    }

    [Fact]
    public async Task ById_WithNoName_Should_NeedAFolder_AndCreateNothing()
    {
        ShareId shareId = await CreateShareAsync();
        Ok(await _aliases.DeleteAsync(ShareName.Create(_name).Value, (await _aliases.GetAsync(ShareName.Create(_name).Value, Ct)).Value!.ETag, Ct));

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ShareReference.For(shareId)))).Error;

        error.Code.ShouldBe("Clone.FolderRequired");
        Directory.EnumerateFileSystemEntries(_work.Path).ShouldBeEmpty();

        // With a folder, it clones.
        Ok(await CloneAsync(new CloneIntoFolderCommand(ShareReference.For(shareId), "deploy"))).Folder.ShouldBe(_work.Combine("deploy"));
    }

    [Fact]
    public async Task ARelativeFolder_Should_BeTakenFromTheCurrentFolder_WithItsParentsCreated()
    {
        await CreateShareAsync();

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName, Path.Combine("a", "b", "config"))));

        cloned.Folder.ShouldBe(_work.Combine(Path.Combine("a", "b", "config")));
        cloned.CreatedFolder.ShouldBeTrue();
        File.Exists(Path.Combine(cloned.Folder, "z.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task AnAbsoluteFolder_Should_BeUsed()
    {
        await CreateShareAsync();
        using TempShareFolder elsewhere = new();
        string absolute = Path.Combine(elsewhere.Path, "config");

        Ok(await CloneAsync(new CloneIntoFolderCommand(ByName, absolute))).Folder.ShouldBe(absolute);
        File.Exists(Path.Combine(absolute, "z.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task AnExistingEmptyFolder_Should_BeClonedInto_AndNotCountAsCreated()
    {
        await CreateShareAsync();
        _work.CreateDirectory("config");

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName, "config")));

        cloned.CreatedFolder.ShouldBeFalse();
        File.Exists(Path.Combine(cloned.Folder, "z.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFolderWithOnlyOsClutter_Should_CountAsEmpty()
    {
        await CreateShareAsync();
        _work.WriteFile("config/.DS_Store", "finder").WriteFile("config/Thumbs.db", "explorer").WriteFile("config/desktop.ini", "x");

        Ok(await CloneAsync(new CloneIntoFolderCommand(ByName, "config"))).CreatedFolder.ShouldBeFalse();
    }

    [Fact]
    public async Task TheCurrentFolder_Should_BeAllowed_WhenEmpty()
    {
        await CreateShareAsync();

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName, ".")));

        cloned.Folder.ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_work.Path)));
        _work.Exists(StateFileNames.State).ShouldBeTrue();
    }

    [Fact]
    public async Task AFolderWithFiles_Should_BeRefused_AndLeftAsItWas()
    {
        await CreateShareAsync();
        _work.WriteFile($"{_name}/mine.txt", "mine");

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ByName))).Error;

        error.Code.ShouldBe("Shares.FolderNotEmpty");
        error.Description.ShouldContain("mysync sync <share> --force");
        Directory.EnumerateFileSystemEntries(_work.Combine(_name)).Select(Path.GetFileName).ShouldBe(["mine.txt"]);
    }

    [Fact]
    public async Task AFile_Should_BeRefused()
    {
        await CreateShareAsync();
        _work.WriteFile(_name, "a file");

        (await CloneAsync(new CloneIntoFolderCommand(ByName))).Error.Code.ShouldBe("Clone.TargetIsAFile");
        _work.ReadText(_name).ShouldBe("a file");
    }

    [Fact]
    public async Task AFolderThatIsAShare_Should_BeRefused()
    {
        ShareId shareId = await CreateShareAsync();

        Error error = (await Send<CloneIntoFolderCommand, CloneIntoFolderResponse>(_laptop, new CloneIntoFolderCommand(ByName, "."))).Error;

        error.Code.ShouldBe("Clone.InsideAShare");
        error.Description.ShouldContain(shareId.ToString());
        error.Description.ShouldContain("already syncs with share");
    }

    [Fact]
    public async Task AFolderInsideAShare_Should_BeRefused_AndNothingIsCreated()
    {
        await CreateShareAsync();

        Error error = (await Send<CloneIntoFolderCommand, CloneIntoFolderResponse>(_laptop, new CloneIntoFolderCommand(ByName, Path.Combine("new", "copy")))).Error;

        error.Code.ShouldBe("Clone.InsideAShare");
        _laptop.Exists("new").ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnsafeRoot_Should_BeRefused()
    {
        await CreateShareAsync();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ByName, home))).Error;

        // The home folder may hold a .mysynctool on a developer's machine; either refusal is right.
        error.Code.ShouldBeOneOf("Shares.UnsafeRoot", "Clone.InsideAShare");
        (await CloneAsync(new CloneIntoFolderCommand(ByName, Path.GetPathRoot(_work.Path)!))).Error.Code.ShouldBeOneOf("Shares.UnsafeRoot", "Clone.InsideAShare");
    }

    [Fact]
    public async Task AFolderThatCantBeCreated_Should_BeCannotCreate_AndLeaveNothing()
    {
        await CreateShareAsync();
        _work.WriteFile("blocker", "a file where a parent folder should be");

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ByName, Path.Combine("blocker", "inner", "config")))).Error;

        error.Code.ShouldBe("Clone.CannotCreate");
        Directory.EnumerateFileSystemEntries(_work.Path).Select(Path.GetFileName).ShouldBe(["blocker"]);
    }

    [Fact]
    public async Task AFolderWithoutPermission_Should_BeCannotCreate()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions");
            return;
        }

        await CreateShareAsync();
        _work.CreateDirectory("locked");
        File.SetUnixFileMode(_work.Combine("locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            (await CloneAsync(new CloneIntoFolderCommand(ByName, Path.Combine("locked", "config")))).Error.Code.ShouldBe("Clone.CannotCreate");
        }
        finally
        {
            File.SetUnixFileMode(_work.Combine("locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("deleted")]
    [InlineData("unfinished")]
    public async Task AShareThatCantBeResolved_Should_CreateNoFolder(string kind)
    {
        string expected;
        switch (kind)
        {
            case "unknown":
                expected = "Shares.NameNotFound";
                break;
            case "deleted":
                ShareId shareId = await CreateShareAsync();
                Ok(await Send<DeleteShareCommand, DeleteShareResponse>(_laptop, new DeleteShareCommand(ShareReference.For(shareId))));
                // The name is freed by delete; by ID, the marker says "deleted".
                Error deleted = (await CloneAsync(new CloneIntoFolderCommand(ShareReference.For(shareId), "x"))).Error;
                deleted.Code.ShouldBe("Shares.Deleted");
                Directory.EnumerateFileSystemEntries(_work.Path).ShouldBeEmpty();
                return;
            default:
                Ok(await _aliases.CreateAsync(new AliasRecord(ShareName.Create(_name).Value, ShareId.New(), DateTimeOffset.UtcNow, "test"), Ct));
                expected = "Shares.Incomplete";
                break;
        }

        (await CloneAsync(new CloneIntoFolderCommand(ByName))).Error.Code.ShouldBe(expected);
        Directory.EnumerateFileSystemEntries(_work.Path).ShouldBeEmpty();
    }

    // Phase 2: the clean-up after a failed or cancelled clone.

    [Fact]
    public async Task AFailingClone_Should_RemoveWhatItWrote_AndTheFoldersItCreated_ButNotParentsThatExisted()
    {
        await CreateShareAsync();
        _work.CreateDirectory("existing");
        string target = _work.Combine(Path.Combine("existing", "new", "config"));
        FailTheStateWriteOnLastDownload(target);
        CloneCleanUpReport? cleanUp = null;

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ByName, target, Parallelism: 1, CleanedUp: report => cleanUp = report))).Error;

        error.Code.ShouldBe("Local.WriteFailed");
        Directory.Exists(_work.Combine(Path.Combine("existing", "new"))).ShouldBeFalse();
        Directory.EnumerateFileSystemEntries(_work.Combine("existing")).ShouldBeEmpty();
        cleanUp.ShouldNotBeNull();
        cleanUp.FolderRemoved.ShouldBeTrue();
        cleanUp.LeftBehind.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailingClone_IntoAnExistingFolder_Should_KeepIt_EmptyOfWhatTheCloneWrote()
    {
        await CreateShareAsync();
        _work.WriteFile("config/.DS_Store", "finder");
        FailTheStateWriteOnLastDownload(_work.Combine("config"));
        CloneCleanUpReport? cleanUp = null;

        (await CloneAsync(new CloneIntoFolderCommand(ByName, "config", Parallelism: 1, CleanedUp: report => cleanUp = report))).IsFailure.ShouldBeTrue();

        Directory.EnumerateFileSystemEntries(_work.Combine("config")).Select(Path.GetFileName).ShouldBe([".DS_Store"]);
        cleanUp!.FolderRemoved.ShouldBeFalse();
        cleanUp.LeftBehind.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStoreThatThrows_Should_StillBeCleanedUp_AndTheExceptionGoesOn()
    {
        await CreateShareAsync();
        int downloads = 0;
        _store.DownloadStream = (_, bytes) => Interlocked.Increment(ref downloads) < 3
            ? new MemoryStream(bytes)
            : throw new InvalidOperationException("the store broke");

        await Should.ThrowAsync<InvalidOperationException>(() => CloneAsync(new CloneIntoFolderCommand(ByName, Parallelism: 1)));

        Directory.EnumerateFileSystemEntries(_work.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task ACancelledClone_Should_RemoveWhatItWrote_AndRethrow()
    {
        await CreateShareAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int downloads = 0;
        _store.DownloadStream = (_, bytes) =>
        {
            if (Interlocked.Increment(ref downloads) == 2)
            {
                cancel.Cancel(); // Ctrl+C half-way through
            }

            return new MemoryStream(bytes);
        };
        CloneCleanUpReport? cleanUp = null;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CloneAsync(new CloneIntoFolderCommand(ByName, Parallelism: 1, CleanedUp: report => cleanUp = report), cancel.Token));

        Directory.EnumerateFileSystemEntries(_work.Path).ShouldBeEmpty();
        cleanUp!.FolderRemoved.ShouldBeTrue();
    }

    [Fact]
    public async Task SomethingElseWrittenDuringTheClone_Should_BeLeftAlone_WithItsFolder_AndReported()
    {
        await CreateShareAsync();
        string target = _work.Combine(_name);
        FailTheStateWriteOnLastDownload(target, also: () =>
        {
            File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
            File.WriteAllText(Path.Combine(target, "docs", "extra.md"), "mine too");
        });
        CloneCleanUpReport? cleanUp = null;

        (await CloneAsync(new CloneIntoFolderCommand(ByName, Parallelism: 1, CleanedUp: report => cleanUp = report))).IsFailure.ShouldBeTrue();

        new TempShareFolderView(target).Files().ShouldBe(new Dictionary<string, string>
        {
            ["docs/extra.md"] = "mine too",
            ["notes.txt"] = "mine",
        });
        cleanUp!.FolderRemoved.ShouldBeFalse();
        cleanUp.LeftBehind.ShouldBe(["docs", "notes.txt"]);
    }

    [Fact]
    public async Task AFileWithOtherContentAtASharePath_Should_BeLeftAlone()
    {
        await CreateShareAsync();
        string target = _work.Combine(_name);
        // Someone replaces a file the clone wrote, before the clone fails.
        FailTheStateWriteOnLastDownload(target, also: () => File.WriteAllText(Path.Combine(target, "docs", "readme.md"), "edited meanwhile"));
        CloneCleanUpReport? cleanUp = null;

        (await CloneAsync(new CloneIntoFolderCommand(ByName, Parallelism: 1, CleanedUp: report => cleanUp = report))).IsFailure.ShouldBeTrue();

        new TempShareFolderView(target).Files().ShouldBe(new Dictionary<string, string> { ["docs/readme.md"] = "edited meanwhile" });
        cleanUp!.LeftBehind.ShouldBe(["docs"]);
    }

    [Fact]
    public async Task AFileThatCantBeDeleted_Should_BeReported_AfterTheOriginalError()
    {
        await CreateShareAsync();
        string target = _work.Combine(_name);
        string docs = Path.Combine(target, "docs");
        FileStream? held = null;
        FailTheStateWriteOnLastDownload(target, also: () =>
        {
            if (OperatingSystem.IsWindows())
            {
                held = new FileStream(Path.Combine(docs, "readme.md"), FileMode.Open, FileAccess.Read, FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(docs, UnixFileMode.UserRead | UnixFileMode.UserExecute); // its files can't be deleted
            }
        });
        CloneCleanUpReport? cleanUp = null;
        try
        {
            Result<CloneIntoFolderResponse> result = await CloneAsync(new CloneIntoFolderCommand(ByName, Parallelism: 1, CleanedUp: report => cleanUp = report));

            result.Error.Code.ShouldBe("Local.WriteFailed"); // the clone's own error, unchanged
            cleanUp!.FolderRemoved.ShouldBeFalse();
            cleanUp.LeftBehind.Count.ShouldBe(1);
            cleanUp.LeftBehind[0].ShouldContain("docs/readme.md");
            File.Exists(Path.Combine(target, "z.txt")).ShouldBeFalse(); // the rest went
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
            if (!OperatingSystem.IsWindows() && Directory.Exists(docs))
            {
                File.SetUnixFileMode(docs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task ALockedFolder_Should_FailWithTheClonesOwnError_AndKeepWhatWasThere()
    {
        await CreateShareAsync();
        string target = _work.Combine(Path.Combine("a", "b"));
        // Another mysync holds the (otherwise empty) folder's lock.
        Directory.CreateDirectory(target);
        await using FileStream held = new(Path.Combine(target, StateFileNames.Lock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CloneCleanUpReport? cleanUp = null;

        Error error = (await CloneAsync(new CloneIntoFolderCommand(ByName, target, CleanedUp: report => cleanUp = report))).Error;

        error.Code.ShouldBe("State.FolderLocked");
        File.Exists(Path.Combine(target, StateFileNames.Lock)).ShouldBeTrue(); // it was there before: not the clone's
        cleanUp!.FolderRemoved.ShouldBeFalse();
    }

    [Fact]
    public async Task AFinishedCloneWithSkippedPaths_Should_KeepEverything()
    {
        await CreateShareAsync();
        _store.DownloadStream = (_, bytes) => bytes.SequenceEqual("last"u8.ToArray()) ? new FailingStream(bytes, 2) : new MemoryStream(bytes);

        CloneIntoFolderResponse cloned = Ok(await CloneAsync(new CloneIntoFolderCommand(ByName)));

        cloned.Report.Skipped.Select(skip => skip.Path).ShouldBe(["z.txt"]);
        File.Exists(Path.Combine(cloned.Folder, "docs", "readme.md")).ShouldBeTrue();
        File.Exists(Path.Combine(cloned.Folder, StateFileNames.State)).ShouldBeTrue();
    }

    [Fact]
    public async Task SyncWithAShare_InAnEmptyFolder_Should_StillCloneThere()
    {
        await CreateShareAsync();

        Ok(await Send<CloneShareCommand, SyncReport>(_work, new CloneShareCommand(ByName)));

        _work.ReadText("z.txt").ShouldBe("last");
    }

    /// <summary>
    /// On the last download (z.txt, with one transfer at a time), a folder where the state file's
    /// temporary copy goes: everything is downloaded, then writing <c>.mysynctool</c> fails.
    /// </summary>
    private void FailTheStateWriteOnLastDownload(string target, Action? also = null)
    {
        _store.DownloadStream = (_, bytes) =>
        {
            if (bytes.SequenceEqual("last"u8.ToArray()))
            {
                Directory.CreateDirectory(Path.Combine(target, StateFileNames.StateTemp, "blocker"));
                also?.Invoke();
            }

            return new MemoryStream(bytes);
        };
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _work.Dispose();
    }

    private Task<Result<TResponse>> Send<TCommand, TResponse>(TempShareFolder folder, TCommand command, CancellationToken? cancellationToken = null)
        where TCommand : ICommand<TResponse> => Send<TCommand, TResponse>(folder.Path, command, cancellationToken);

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(string folder, TCommand command, CancellationToken? cancellationToken = null)
        where TCommand : ICommand<TResponse>
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new ShareRoot(folder));
        services.AddSingleton<IShareStore>(_store);
        services.AddSingleton<IAliasStore>(_aliases);
        services.AddSingleton<TimeProvider>(new InstantTimeProvider());
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.ISiteAssetSource>(new InMemorySiteAssetSource());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.IPublishTarget>(provider =>
            new InMemoryPublishTarget((InMemoryShareStore)provider.GetRequiredService<IShareStore>()));
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(provider => new MySync.Cli.Hosting.FolderCloneRunner(services, provider));
        services.AddApplication();
        services.AddInfrastructure();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, cancellationToken ?? Ct);
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    private static void Ok(Result result) =>
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);

    /// <summary>The files under a folder (without mysync's own), like <see cref="TempShareFolder.Files"/>.</summary>
    private sealed class TempShareFolderView(string root)
    {
        public Dictionary<string, string> Files() =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(path => !path.StartsWith(".mysynctool", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)));
    }
}
