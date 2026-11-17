using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.TestSupport;

/// <summary>
/// One shared in-memory store and clock, and any number of "machines" syncing through it. Each
/// machine has its own folder, state, lock and conflict log, and resolves the real handlers
/// (with their decorators) from <c>AddApplication()</c>.
/// </summary>
internal sealed class SyncWorld
{
    public FixedDateTimeProvider Clock { get; } = new(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));

    public InstantTimeProvider Time { get; } = new();

    public InMemoryShareStore Store { get; }

    /// <summary>Parallelism 1 by default, so progress sequences are deterministic.</summary>
    public SyncOptions Options { get; set; } = new() { MaxParallelism = 1 };

    public SyncWorld() => Store = new InMemoryShareStore(Clock);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Machine Machine(string name, InMemoryLocalFolder? folder = null) => new(this, name, folder ?? new InMemoryLocalFolder(Clock));

    public async Task<Manifest> HeadAsync(ShareId shareId) => (await Store.GetManifestAsync(shareId, Ct)).Value!.Manifest;

    /// <summary>Commits HEAD again as a new revision from "elsewhere": same entries, new ETag.</summary>
    public async Task CommitElsewhereAsync(ShareId shareId)
    {
        Func<ShareId, Task>? hook = Store.BeforeReplaceManifest;
        Store.BeforeReplaceManifest = null;
        try
        {
            ManifestSnapshot head = (await Store.GetManifestAsync(shareId, Ct)).Value!;
            Manifest next = head.Manifest.CreateNextRevision(head.Manifest.Entries, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero), "other@ELSEWHERE").Value;
            (await Store.ReplaceManifestAsync(shareId, next, head.ETag, Ct)).IsSuccess.ShouldBeTrue();
        }
        finally
        {
            Store.BeforeReplaceManifest = hook;
        }
    }
}

internal sealed class Machine
{
    private readonly ServiceProvider _services;

    public Machine(SyncWorld world, string name, InMemoryLocalFolder folder)
    {
        Name = name;
        Folder = folder;

        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IShareStore>(world.Store);
        services.AddSingleton<ILocalFolder>(folder);
        services.AddSingleton<IFileScanner>(folder);
        services.AddSingleton<IShareStateStore>(State);
        services.AddSingleton<IFolderLock>(Lock);
        services.AddSingleton<IConflictLog>(ConflictLog);
        services.AddSingleton<IIgnoreRulesLoader>(IgnoreRules);
        services.AddSingleton<IMachineIdentity>(new FixedMachineIdentity(name));
        services.AddSingleton<IDateTimeProvider>(world.Clock);
        services.AddSingleton<IContentHasher>(new TestContentHasher());
        services.AddSingleton<TimeProvider>(world.Time);
        services.AddSingleton(_ => world.Options);
        services.AddApplication();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public string Name { get; }

    public InMemoryLocalFolder Folder { get; }

    public InMemoryShareStateStore State { get; } = new();

    public InMemoryFolderLock Lock { get; } = new();

    public RecordingConflictLog ConflictLog { get; } = new();

    public FixedIgnoreRulesLoader IgnoreRules { get; } = new(PathListIgnoreMatcher.BuiltInsOnly);

    public ShareId ShareId => State.State!.ShareId;

    public Task<Result<NewSharePreview>> PreviewAsync() => Query<PreviewNewShareQuery, NewSharePreview>(new PreviewNewShareQuery());

    public Task<Result<CreateShareResponse>> CreateAsync(CreateShareCommand? command = null) =>
        Send<CreateShareCommand, CreateShareResponse>(command ?? new CreateShareCommand());

    public Task<Result<SyncReport>> CloneAsync(ShareId shareId, bool force = false) =>
        Send<CloneShareCommand, SyncReport>(new CloneShareCommand(shareId.ToString(), force));

    public Task<Result<SyncReport>> CloneAsync(CloneShareCommand command) => Send<CloneShareCommand, SyncReport>(command);

    public Task<Result<SyncReport>> SyncAsync(RunSyncCommand? command = null) => Send<RunSyncCommand, SyncReport>(command ?? new RunSyncCommand());

    /// <summary>Text of every file in the folder, by path.</summary>
    public IReadOnlyDictionary<string, string> Files() =>
        Folder.AllPaths.Where(path => !Folder.IsDirectory(path)).ToDictionary(path => path, Folder.ReadText, StringComparer.Ordinal);

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, SyncWorld.Ct);
    }

    private async Task<Result<TResponse>> Query<TQuery, TResponse>(TQuery query)
        where TQuery : IQuery<TResponse>
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResponse>>().Handle(query, SyncWorld.Ct);
    }

    public override string ToString() => Name;
}

internal static class ResultExtensions
{
    public static T ShouldSucceed<T>(this Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    public static Error ShouldFail<T>(this Result<T> result)
    {
        result.IsFailure.ShouldBeTrue("expected a failure");
        return result.Error;
    }
}
