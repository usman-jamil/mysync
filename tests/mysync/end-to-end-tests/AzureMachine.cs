using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Sync.Run;
using MySync.Domain.Shares;
using MySync.Infrastructure;
using MySync.Infrastructure.FileSystem;
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<ShareId> CreateAsync() =>
        Ok(await SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand())).ShareId;

    public Task<Result<SyncReport>> CloneAsync(ShareId shareId) => SendAsync<CloneShareCommand, SyncReport>(new CloneShareCommand(shareId.ToString()));

    public Task<Result<SyncReport>> SyncAsync() => SendAsync<RunSyncCommand, SyncReport>(new RunSyncCommand());

    public Task<Result<DeleteShareResponse>> DeleteAsync(string? shareId = null) =>
        SendAsync<DeleteShareCommand, DeleteShareResponse>(new DeleteShareCommand(shareId));

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
        }).Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton(new ShareRoot(Folder.Path));
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddApplication().AddInfrastructure().AddAzureBlobStorage();
        return services;
    }
}
