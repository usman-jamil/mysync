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
using MySync.Application.Conflicts.List;
using MySync.Application.Conflicts.Resolve;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync;
using MySync.Application.Sync.Plan;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using MySync.Application.Shares.Rename;
using MySync.Application.Shares.List;
using MySync.Application.Publishing.List;
using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;

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

    /// <summary>Share names, shared by every machine like the store.</summary>
    public InMemoryAliasStore Aliases { get; } = new();

    private int _names;

    /// <summary>A fresh, unused name, for tests that create shares without caring what they're called.</summary>
    public ShareName NextName() => ShareName.Create($"share-{Interlocked.Increment(ref _names):D3}").Value;

    /// <summary>Parallelism 1 by default, so progress sequences are deterministic.</summary>
    public SyncOptions Options { get; set; } = new() { MaxParallelism = 1 };

    public SyncWorld()
    {
        Store = new InMemoryShareStore(Clock);
        Publishing = new InMemoryPublishTarget(Store);
        Verifier = new InMemoryPublicationVerifier(Publishing);
    }

    /// <summary>The publishing account's static website, shared by every machine like the store. Replaceable before a machine publishes.</summary>
    public InMemoryPublishTarget Publishing { get; set; }

    public InMemoryPublicationVerifier Verifier { get; set; }

    /// <summary>The site assets every machine's binary carries (set a placeholder to simulate a Debug build).</summary>
    public InMemorySiteAssetSource SiteAssets { get; set; } = new();

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
    private readonly SyncWorld _world;

    public Machine(SyncWorld world, string name, InMemoryLocalFolder folder)
    {
        _world = world;
        Name = name;
        Folder = folder;
        Sources = new InMemoryLocalSources(world.Clock);
        Destinations = new InMemoryLocalFolderFactory(world.Clock);

        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IShareStore>(world.Store);
        services.AddSingleton<IAliasStore>(world.Aliases);
        services.AddSingleton<ILocalFolder>(folder);
        services.AddSingleton<IFileScanner>(folder);
        services.AddSingleton<ILocalSources>(Sources);
        services.AddSingleton<ILocalFolderFactory>(Destinations);
        services.AddSingleton<IPushRecordSerializer>(PushRecords);
        services.AddSingleton<IShareStateStore>(State);
        services.AddSingleton<IFolderLock>(Lock);
        services.AddSingleton<IConflictLog>(ConflictLog);
        services.AddSingleton<IIgnoreRulesLoader>(IgnoreRules);
        services.AddSingleton<IMachineIdentity>(new FixedMachineIdentity(name));
        services.AddSingleton<IDateTimeProvider>(world.Clock);
        services.AddSingleton<IContentHasher>(new TestContentHasher());
        services.AddSingleton<TimeProvider>(world.Time);
        services.AddSingleton(_ => world.Options);
        services.AddSingleton<Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<Abstractions.Updates.IExecutableInstaller>(new NotInstalled());
        services.AddScoped<Abstractions.Publishing.ISiteAssetSource>(_ => world.SiteAssets);
        services.AddScoped<Abstractions.Publishing.IPublishTarget>(_ => world.Publishing);
        services.AddScoped<Abstractions.Publishing.IPublicationVerifier>(_ => world.Verifier);
        services.AddSingleton<Abstractions.Publishing.ISiteDataSerializer>(new TestSiteDataSerializer());
        services.AddApplication();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public string Name { get; }

    public InMemoryLocalFolder Folder { get; }

    /// <summary>The machine's disk as <c>mysync push</c> sees it (absolute paths; push runs in <see cref="InMemoryLocalSources.CurrentDirectory"/>).</summary>
    public InMemoryLocalSources Sources { get; }

    public TestPushRecordSerializer PushRecords { get; } = new();

    /// <summary>Where <c>mysync pull</c> writes: one in-memory folder per destination path (default <see cref="InMemoryLocalFolderFactory.CurrentDirectory"/>).</summary>
    public InMemoryLocalFolderFactory Destinations { get; }

    public InMemoryShareStateStore State { get; } = new();

    public InMemoryFolderLock Lock { get; } = new();

    public RecordingConflictLog ConflictLog { get; } = new();

    public FixedIgnoreRulesLoader IgnoreRules { get; } = new(PathListIgnoreMatcher.BuiltInsOnly);

    public ShareId ShareId => State.State!.ShareId;

    public Task<Result<NewSharePreview>> PreviewAsync() => Query<PreviewNewShareQuery, NewSharePreview>(new PreviewNewShareQuery());

    /// <summary>Creates a share; without a name in <paramref name="command"/>, a fresh one is made up.</summary>
    public Task<Result<CreateShareResponse>> CreateAsync(CreateShareCommand? command = null) =>
        Send<CreateShareCommand, CreateShareResponse>((command ?? new CreateShareCommand()) is { Name: null } unnamed ? unnamed with { Name = _world.NextName() } : command!);

    /// <summary>Creates a share with the command exactly as given (e.g. no name).</summary>
    public Task<Result<CreateShareResponse>> CreateExactlyAsync(CreateShareCommand command) => Send<CreateShareCommand, CreateShareResponse>(command);

    public Task<Result<SyncReport>> CloneAsync(ShareId shareId, bool force = false) =>
        Send<CloneShareCommand, SyncReport>(new CloneShareCommand(ShareReference.For(shareId), force));

    public Task<Result<SyncReport>> CloneAsync(string shareOrName, bool force = false) =>
        Send<CloneShareCommand, SyncReport>(new CloneShareCommand(ShareReference.Parse(shareOrName).Value, force));

    public Task<Result<RenameShareResponse>> RenameAsync(string newName) =>
        Send<RenameShareCommand, RenameShareResponse>(new RenameShareCommand(ShareName.Create(newName).Value));

    public Task<Result<ShareListResponse>> SharesAsync() => Query<ListSharesQuery, ShareListResponse>(new ListSharesQuery());

    public Task<Result<ShareInfoResponse>> InfoAsync(string shareOrName) =>
        Query<GetShareInfoQuery, ShareInfoResponse>(new GetShareInfoQuery(ShareReference.Parse(shareOrName).Value));

    public Task<Result<SyncReport>> CloneAsync(CloneShareCommand command) => Send<CloneShareCommand, SyncReport>(command);

    public Task<Result<SyncReport>> SyncAsync(RunSyncCommand? command = null) => Send<RunSyncCommand, SyncReport>(command ?? new RunSyncCommand());

    public Task<Result<SyncReport>> StatusAsync() => Query<GetSyncPlanQuery, SyncReport>(new GetSyncPlanQuery());

    public Task<Result<ShareInfoResponse>> InfoAsync() => Query<GetShareInfoQuery, ShareInfoResponse>(new GetShareInfoQuery());

    public Task<Result<MySync.Application.Shares.Compression.CompressionResponse>> SetCompressionAsync(bool on, string? shareOrName = null) =>
        Send<MySync.Application.Shares.Compression.SetCompressionCommand, MySync.Application.Shares.Compression.CompressionResponse>(new(on, Reference(shareOrName)));

    public Task<Result<MySync.Application.Shares.Compression.CompressionResponse>> CompressionAsync(string? shareOrName = null) =>
        Query<MySync.Application.Shares.Compression.GetCompressionQuery, MySync.Application.Shares.Compression.CompressionResponse>(new(Reference(shareOrName)));

    public Task<Result<MySync.Application.Conflicts.Diff.DiffResponse>> DiffAsync(MySync.Application.Conflicts.Diff.DiffQuery query) =>
        Query<MySync.Application.Conflicts.Diff.DiffQuery, MySync.Application.Conflicts.Diff.DiffResponse>(query);

    public Task<Result<Stream>> PrintAsync(string path, MySync.Application.Conflicts.Diff.DiffSideKind side) =>
        Query<MySync.Application.Conflicts.Diff.PrintVersionQuery, Stream>(new MySync.Application.Conflicts.Diff.PrintVersionQuery(path, side));

    public Task<Result<ConflictListResponse>> ConflictsAsync() => Query<ListConflictsQuery, ConflictListResponse>(new ListConflictsQuery());

    public Task<Result<SyncReport>> ResolveAsync(string keep, params string[] paths) =>
        Send<ResolveConflictsCommand, SyncReport>(new ResolveConflictsCommand(paths, All: false, keep));

    public Task<Result<SyncReport>> ResolveAsync(ResolveConflictsCommand command) => Send<ResolveConflictsCommand, SyncReport>(command);

    public Task<Result<DeleteShareResponse>> DeleteAsync(string? shareOrName = null) =>
        Send<DeleteShareCommand, DeleteShareResponse>(new DeleteShareCommand(Reference(shareOrName)));

    public Task<Result<PublishReport>> PublishAsync(PublishShareCommand? command = null, CancellationToken? cancellationToken = null) =>
        Send<PublishShareCommand, PublishReport>(command ?? new PublishShareCommand(), cancellationToken);

    public Task<Result<PublicationListResponse>> PublicationsAsync(string? shareId = null) =>
        Query<ListPublicationsQuery, PublicationListResponse>(new ListPublicationsQuery(Reference(shareId)));

    public Task<Result<UnpublishReport>> UnpublishAsync(string publishId, string? shareId = null) =>
        Send<UnpublishCommand, UnpublishReport>(new UnpublishCommand(publishId, Reference(shareId)));

    public Task<Result<MySync.Application.Sync.Push.PushReport>> PushAsync(MySync.Application.Sync.Push.PushCommand command, CancellationToken? cancellationToken = null) =>
        Send<MySync.Application.Sync.Push.PushCommand, MySync.Application.Sync.Push.PushReport>(command, cancellationToken);

    /// <summary><c>mysync push &lt;share&gt; [&lt;path&gt;...]</c> with every option at its default.</summary>
    public Task<Result<MySync.Application.Sync.Push.PushReport>> PushAsync(string share, params string[] paths) =>
        PushAsync(new MySync.Application.Sync.Push.PushCommand(ShareReference.Parse(share).Value, paths));

    public Task<Result<MySync.Application.Sync.Pull.PullReport>> PullAsync(MySync.Application.Sync.Pull.PullCommand command, CancellationToken? cancellationToken = null) =>
        Send<MySync.Application.Sync.Pull.PullCommand, MySync.Application.Sync.Pull.PullReport>(command, cancellationToken);

    /// <summary><c>mysync pull &lt;share&gt; [&lt;share-path&gt;...]</c> into the current folder, every option at its default.</summary>
    public Task<Result<MySync.Application.Sync.Pull.PullReport>> PullAsync(string share, params string[] paths) =>
        PullAsync(new MySync.Application.Sync.Pull.PullCommand(ShareReference.Parse(share).Value, paths));

    public Task<Result<GarbageReport>> GcAsync(CollectGarbageCommand? command = null) =>
        Send<CollectGarbageCommand, GarbageReport>(command ?? new CollectGarbageCommand());

    /// <summary>Text of every file in the folder, by path.</summary>
    public IReadOnlyDictionary<string, string> Files() =>
        Folder.AllPaths.Where(path => !Folder.IsDirectory(path)).ToDictionary(path => path, Folder.ReadText, StringComparer.Ordinal);

    private static ShareReference? Reference(string? shareOrName) => shareOrName is null ? null : ShareReference.Parse(shareOrName).Value;

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(TCommand command, CancellationToken? cancellationToken = null)
        where TCommand : ICommand<TResponse>
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, cancellationToken ?? SyncWorld.Ct);
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
