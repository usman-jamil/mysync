using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.List;
using MySync.Application.Shares.Rename;
using MySync.Application.Sync.Run;
using MySync.Domain.Shares;
using MySync.Infrastructure;
using MySync.Infrastructure.FileSystem;
using MySync.Application.Publishing.List;
using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;
using MySync.SiteAssets;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// One "machine": a real temp folder and the full DI graph the CLI builds (Application,
/// Infrastructure, the provider), pointed at the test storage through configuration keys named
/// like the environment variables. Each command gets a fresh composition, as each CLI run would.
/// </summary>
internal sealed class Machine(Backend backend, string place) : IDisposable
{
    public TempShareFolder Folder { get; } = new();

    /// <summary>The share provider; another one makes this folder's next command see another storage.</summary>
    public Backend Backend { get; set; } = backend;

    /// <summary>The isolated place in <see cref="Backend"/> (an Azure container, an S3 key prefix).</summary>
    public string Place { get; set; } = place;

    /// <summary>Where publications go (MYSYNC_PUBLISH_PROVIDER and its settings); <c>null</c> leaves the publishing settings unset.</summary>
    public Backend? Publishing { get; set; }

    /// <summary>Settings applied over the backend's (e.g. <see cref="Backend.Elsewhere"/>).</summary>
    public Dictionary<string, string?> Overrides { get; } = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates a share named <paramref name="name"/>, or a fresh unique name.</summary>
    public async Task<ShareId> CreateAsync(string? name = null) =>
        Ok(await CreateNamedAsync(name ?? "e2e-" + Guid.NewGuid().ToString("N")[..12])).ShareId;

    public Task<Result<CreateShareResponse>> CreateNamedAsync(string name) =>
        SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(ShareName.Create(name).Value));

    /// <summary>Creates a share with compression on (<c>sync --compress</c>).</summary>
    public Task<Result<CreateShareResponse>> CreateCompressedAsync(string? name = null) =>
        SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(ShareName.Create(name ?? "e2e-" + Guid.NewGuid().ToString("N")[..12]).Value, Compress: true));

    public Task<Result<MySync.Application.Shares.Compression.CompressionResponse>> SetCompressionAsync(bool on) =>
        SendAsync<MySync.Application.Shares.Compression.SetCompressionCommand, MySync.Application.Shares.Compression.CompressionResponse>(new(on));

    public Task<Result<SyncReport>> ResolveAsync(string keep, params string[] paths) =>
        SendAsync<MySync.Application.Conflicts.Resolve.ResolveConflictsCommand, SyncReport>(new([.. paths], false, keep));

    public Task<Result<SyncReport>> CloneAsync(ShareId shareId) => SendAsync<CloneShareCommand, SyncReport>(new CloneShareCommand(ShareReference.For(shareId)));

    public Task<Result<SyncReport>> CloneAsync(string shareOrName) => SendAsync<CloneShareCommand, SyncReport>(new CloneShareCommand(ShareReference.Parse(shareOrName).Value));

    public Task<Result<RenameShareResponse>> RenameAsync(string newName) =>
        SendAsync<RenameShareCommand, RenameShareResponse>(new RenameShareCommand(ShareName.Create(newName).Value));

    public async Task<Result<ShareListResponse>> SharesAsync()
    {
        await using ServiceProvider provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<ListSharesQuery, ShareListResponse>>().Handle(new ListSharesQuery(), Ct);
    }

    public Task<Result<SyncReport>> SyncAsync() => SendAsync<RunSyncCommand, SyncReport>(new RunSyncCommand());

    /// <summary><c>mysync push</c>, run in this machine's folder (relative paths are against it).</summary>
    public Task<Result<MySync.Application.Sync.Push.PushReport>> PushAsync(MySync.Application.Sync.Push.PushCommand command) =>
        SendAsync<MySync.Application.Sync.Push.PushCommand, MySync.Application.Sync.Push.PushReport>(command);

    public Task<Result<MySync.Application.Sync.Pull.PullReport>> PullAsync(MySync.Application.Sync.Pull.PullCommand command) =>
        SendAsync<MySync.Application.Sync.Pull.PullCommand, MySync.Application.Sync.Pull.PullReport>(command);

    public Task<Result<DeleteShareResponse>> DeleteAsync(string? shareId = null) =>
        SendAsync<DeleteShareCommand, DeleteShareResponse>(new DeleteShareCommand(Reference(shareId)));

    public Task<Result<PublishReport>> PublishAsync(PublishShareCommand? command = null) =>
        SendAsync<PublishShareCommand, PublishReport>(command ?? new PublishShareCommand());

    public Task<Result<UnpublishReport>> UnpublishAsync(string publishId, string? shareId = null) =>
        SendAsync<UnpublishCommand, UnpublishReport>(new UnpublishCommand(publishId, Reference(shareId)));

    public async Task<Result<PublicationListResponse>> PublicationsAsync(string? shareId = null)
    {
        await using ServiceProvider provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<ListPublicationsQuery, PublicationListResponse>>().Handle(new ListPublicationsQuery(Reference(shareId)), Ct);
    }

    private static ShareReference? Reference(string? shareOrName) => shareOrName is null ? null : ShareReference.Parse(shareOrName).Value;

    /// <summary>The store this machine's commands use, for looking at the remote side directly.</summary>
    public async Task<T> WithStoreAsync<T>(Func<IShareStore, Task<T>> look)
    {
        ArgumentNullException.ThrowIfNull(look);
        await using ServiceProvider provider = Services().BuildServiceProvider();
        return await look(provider.GetRequiredService<IShareStore>());
    }

    /// <summary>This machine's full composition, kept for as long as the caller needs it (one <c>mysync tui</c> session).</summary>
    public ServiceProvider BuildProvider() => Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    public void Dispose() => Folder.Dispose();

    public static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    private async Task<Result<TResponse>> SendAsync<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
    {
        await using ServiceProvider provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, Ct);
    }

    /// <summary>The settings this machine's commands see, as a CLI process would get them in its environment.</summary>
    public Dictionary<string, string?> Environment()
    {
        Dictionary<string, string?> settings = Backend.Settings(Place);
        if (Publishing is not null)
        {
            foreach ((string name, string? value) in Publishing.PublishSettings())
            {
                settings[name] = value;
            }

            settings["MYSYNC_PUBLISH_PROVIDER"] = Publishing.Provider;
        }

        foreach ((string name, string? value) in Overrides)
        {
            settings[name] = value;
        }

        return settings;
    }

    private ServiceCollection Services()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Environment()).Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton(new ShareRoot(Folder.Path));
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(provider => new MySync.Cli.Hosting.FolderCloneRunner(services, provider));

        services.AddApplication().AddInfrastructure().AddSiteAssets();
        Backend.Register(services, Place);
        (Publishing ?? Backend).RegisterPublishing(services);
        return services;
    }
}
