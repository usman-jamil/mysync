using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Session;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Domain.Shares;
using MySync.Testing;
using MySync.Tui.Rendering;
using MySync.Tui.Runtime;
using MySync.Tui.State;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Tui.UnitTests;

/// <summary>Machines sharing one in-memory store, each with the real handlers (as the application tests' SyncWorld).</summary>
internal sealed class TuiWorld
{
    public FixedDateTimeProvider Clock { get; } = new(new DateTime(2026, 11, 23, 11, 0, 0, DateTimeKind.Utc));

    public InMemoryShareStore Store { get; }

    public InMemoryAliasStore Aliases { get; } = new();

    public TuiWorld() => Store = new InMemoryShareStore(Clock);

    public TuiMachine Machine(string name) => new(this, name);
}

internal sealed class TuiMachine
{
    private readonly ServiceProvider _services;

    public TuiMachine(TuiWorld world, string name)
    {
        Name = name;
        Folder = new InMemoryLocalFolder(world.Clock) { RootPath = "/home/usman/app-config" };
        Sources = new InMemoryLocalSources(world.Clock) { CurrentDirectory = "/home/usman/app-config" };

        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IShareStore>(world.Store);
        services.AddSingleton<IAliasStore>(world.Aliases);
        services.AddSingleton<ILocalFolder>(Folder);
        services.AddSingleton<IFileScanner>(Folder);
        services.AddSingleton<ILocalSources>(Sources);
        services.AddSingleton<ILocalFolderFactory>(new InMemoryLocalFolderFactory(world.Clock));
        services.AddSingleton<IPushRecordSerializer>(new TestPushRecordSerializer());
        services.AddSingleton<IShareStateStore>(new InMemoryShareStateStore());
        services.AddSingleton<IFolderLock>(new InMemoryFolderLock());
        services.AddSingleton<IConflictLog>(new RecordingConflictLog());
        services.AddSingleton<IIgnoreRulesLoader>(new FixedIgnoreRulesLoader(PathListIgnoreMatcher.BuiltInsOnly));
        services.AddSingleton<IMachineIdentity>(new FixedMachineIdentity(name));
        services.AddSingleton<IDateTimeProvider>(world.Clock);
        services.AddSingleton<IContentHasher>(new TestContentHasher());
        services.AddSingleton<TimeProvider>(new InstantTimeProvider());
        services.AddSingleton(new MySync.Application.Sync.SyncOptions());
        services.AddSingleton<Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<Application.Abstractions.Updates.IExecutableInstaller>(new NotInstalled());
        services.AddSingleton<Application.Abstractions.Publishing.ISiteAssetSource>(new InMemorySiteAssetSource());
        services.AddSingleton<Application.Abstractions.Publishing.IPublishTarget>(new InMemoryPublishTarget(world.Store));
        services.AddSingleton<Application.Abstractions.Publishing.IPublicationVerifier>(provider =>
            new InMemoryPublicationVerifier((InMemoryPublishTarget)provider.GetRequiredService<Application.Abstractions.Publishing.IPublishTarget>()));
        services.AddSingleton<Application.Abstractions.Publishing.ISiteDataSerializer>(new TestSiteDataSerializer());
        services.AddSingleton<MySync.Application.Abstractions.FileSystem.ICloneFolders>(new NoCloneFolders());
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(new NoCloneRunner());
        services.AddApplication();
        _services = services.BuildServiceProvider();
    }

    public string Name { get; }

    public InMemoryLocalFolder Folder { get; }

    public InMemoryLocalSources Sources { get; }

    public IFolderSession Session => _services.GetRequiredService<IFolderSession>();

    /// <summary>Writes a file both where sync reads it and where push reads it.</summary>
    public TuiMachine Write(string path, string content)
    {
        Folder.WriteFile(path, content);
        Sources.WriteFile($"{Sources.CurrentDirectory}/{path}", content);
        return this;
    }

    public async Task<ShareId> CreateAsync(string name) =>
        (await Send<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(ShareName.Create(name).Value))).Value.ShareId;

    public async Task CloneAsync(string name) =>
        (await Send<CloneShareCommand, SyncReport>(new CloneShareCommand(ShareReference.Parse(name).Value))).IsSuccess.ShouldBeTrue();

    public async Task SyncAsync() => (await Send<RunSyncCommand, SyncReport>(new RunSyncCommand())).IsSuccess.ShouldBeTrue();

    public Task<Result<InitializeEmptyShareResponse>> InitializeAsync(string name) =>
        Send<InitializeEmptyShareCommand, InitializeEmptyShareResponse>(new InitializeEmptyShareCommand(ShareName.Create(name).Value));

    public TuiRuntime Runtime(ScriptedTerminal terminal, Func<TuiState, Frame>? render = null) =>
        new(terminal, Session, new TuiOptions(false, ColorDepth.TrueColor, Folder.RootPath, "APP-SRV-02", Name), TimeProvider.System, render: render);

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, TestContext.Current.CancellationToken);
    }
}
