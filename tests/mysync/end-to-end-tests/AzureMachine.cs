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
using MySync.Storage.Azure;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// One "machine": a real temp folder and the full DI graph the CLI builds (Application,
/// Infrastructure, Azure), pointed at Azurite through configuration keys named like the
/// environment variables. Each command gets a fresh container, as each CLI run would.
/// </summary>
internal sealed class AzureMachine(string connectionString, string container) : IDisposable
{
    public TempShareFolder Folder { get; } = new();

    public string Container { get; set; } = container;

    /// <summary>MYSYNC_PUBLISH_CONNECTION_STRING for this machine; <c>null</c> leaves it unset.</summary>
    public string? PublishConnectionString { get; set; }

    public const string PublishBaseUrl = "https://mysyncpublish.z13.web.core.windows.net";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates a share named <paramref name="name"/>, or a fresh unique name.</summary>
    public async Task<ShareId> CreateAsync(string? name = null) =>
        Ok(await CreateNamedAsync(name ?? "e2e-" + Guid.NewGuid().ToString("N")[..12])).ShareId;

    public Task<Result<CreateShareResponse>> CreateNamedAsync(string name) =>
        SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(ShareName.Create(name).Value));

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

    private ServiceCollection Services()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AzureStorageOptions.ConnectionStringVariable] = connectionString,
            [AzureStorageOptions.ContainerVariable] = Container,
            [PublishStorageOptions.ConnectionStringVariable] = PublishConnectionString,
            [PublishStorageOptions.BaseUrlVariable] = PublishConnectionString is null ? null : PublishBaseUrl,
        }).Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton(new ShareRoot(Folder.Path));
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        if (PublishConnectionString is not null)
        {
            // Azurite has no static website endpoint: read the $web blob the URL names, as the endpoint would serve it.
            services.AddSingleton<MySync.Application.Abstractions.Publishing.IPublicationVerifier>(new AzuriteWebsiteVerifier(PublishConnectionString));
        }

        services.AddApplication().AddInfrastructure().AddAzureBlobStorage().AddAzureStaticWebsitePublishing().AddSiteAssets();
        return services;
    }
}
