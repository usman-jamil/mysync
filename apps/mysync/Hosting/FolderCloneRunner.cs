using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Composition;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Shares.Clone;
using MySync.Application.Sync.Run;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Cli.Hosting;

/// <summary>
/// Runs <see cref="CloneShareCommand"/> in another folder: the process's own composition (every
/// registration of <paramref name="services"/>, which is the composition root's whole collection)
/// built again with <see cref="ShareRoot"/> set to that folder. So every folder-bound service
/// (folder, scanner, state file, lock, ignore rules, conflict log) works there, and every other
/// service is composed exactly as for the rest of the process. The current directory never changes.
/// Lives in the composition root, the one project that builds containers; the tests link it in.
/// </summary>
/// <remarks>
/// Logging is the one thing taken from the running process instead (<paramref name="parent"/>'s
/// <see cref="ILoggerFactory"/>): a second logger factory would write to the same sinks and could
/// dispose them when the folder's provider is disposed. Instance registrations (options, clocks,
/// stores a test registered) are shared as they are; factory singletons are built again.
/// </remarks>
internal sealed class FolderCloneRunner(IServiceCollection services, IServiceProvider parent) : ICloneRunner
{
    public async Task<Result<SyncReport>> CloneAsync(string root, CloneShareCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(command);

        IServiceCollection folderServices = new ServiceCollection();
        folderServices.AddSingleton(new ShareRoot(root));
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ShareRoot) && descriptor.ServiceType != typeof(ILoggerFactory))
            {
                folderServices.Add(descriptor);
            }
        }

        if (parent.GetService<ILoggerFactory>() is { } loggers)
        {
            folderServices.AddSingleton(loggers);
        }

        await using ServiceProvider provider = folderServices.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<ICommandHandler<CloneShareCommand, SyncReport>>()
            .Handle(command, cancellationToken);
    }
}
