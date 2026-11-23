using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Abstractions.Updates;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.Hashing;
using MySync.Infrastructure.Identity;
using MySync.Infrastructure.Ignore;
using MySync.Infrastructure.Logging;
using MySync.Infrastructure.Publishing;
using MySync.Infrastructure.Serialization;
using MySync.Infrastructure.State;
using MySync.Infrastructure.Time;
using MySync.Infrastructure.Updates;
using SharedKernel;

namespace MySync.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Everything on the local machine: disk, state file, hashing, ignore rules, conflict log,
    /// manifest JSON, clock. The share root is the current directory unless a
    /// <see cref="ShareRoot"/> is registered first (tests use a temp folder).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.TryAddSingleton(_ => new ShareRoot(Directory.GetCurrentDirectory()));

        services.TryAddSingleton<ILocalFolder>(provider => new LocalFolder(provider.GetRequiredService<ShareRoot>()));
        services.TryAddSingleton<ILocalFolderFactory, LocalFolderFactory>();
        services.TryAddSingleton<IFileScanner, FileScanner>();
        services.TryAddSingleton<ILocalSources, LocalSources>();
        services.TryAddSingleton<IContentHasher, Sha256ContentHasher>();
        services.TryAddSingleton<IShareStateStore, JsonShareStateStore>();
        services.TryAddSingleton<IFolderLock, FolderLock>();
        services.TryAddSingleton<IInterfaceLock, InterfaceLock>();
        services.TryAddSingleton<IIgnoreRulesLoader, GitIgnoreRulesLoader>();
        services.TryAddSingleton<IConflictLog>(provider => new FileConflictLog(provider.GetRequiredService<ShareRoot>()));
        services.TryAddSingleton<IManifestSerializer, ManifestSerializer>();
        services.TryAddSingleton<IPushRecordSerializer, PushRecordSerializer>();
        services.TryAddSingleton<IMachineIdentity, MachineIdentity>();
        services.TryAddSingleton<IExecutableInstaller, ExecutableInstaller>();
        services.TryAddSingleton<IPublicationRegistrySerializer, PublicationRegistrySerializer>();
        services.TryAddSingleton<ISiteDataSerializer, SiteDataSerializer>();
        services.TryAddSingleton(TimeProvider.System);

        // Anonymous on purpose: it checks what the public sees. No handler adds credentials.
        services.TryAddSingleton<IPublicationVerifier>(provider => new HttpPublicationVerifier(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Releases from a local folder (<c>MYSYNC_UPDATE_SOURCE=/path</c>): a mirror, an offline machine,
    /// or tests. The default source is GitHub (<c>MySync.Updates.GitHub</c>).
    /// </summary>
    public static IServiceCollection AddDirectoryReleaseSource(this IServiceCollection services, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        services.TryAddSingleton<IReleaseSource>(new DirectoryReleaseSource(Path.GetFullPath(folder)));
        return services;
    }
}
