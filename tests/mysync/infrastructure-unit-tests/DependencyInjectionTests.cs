using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests;

public sealed class DependencyInjectionTests : IDisposable
{
    private readonly TempShareFolder _temp = new();

    [Fact]
    public void AddInfrastructure_Should_RegisterAUtcClock()
    {
        using ServiceProvider provider = new ServiceCollection().AddInfrastructure().BuildServiceProvider();

        IDateTimeProvider clock = provider.GetRequiredService<IDateTimeProvider>();

        clock.UtcNow.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(typeof(ILocalFolder))]
    [InlineData(typeof(IFileScanner))]
    [InlineData(typeof(ILocalSources))]
    [InlineData(typeof(IPushRecordSerializer))]
    [InlineData(typeof(IContentHasher))]
    [InlineData(typeof(IShareStateStore))]
    [InlineData(typeof(IFolderLock))]
    [InlineData(typeof(IIgnoreRulesLoader))]
    [InlineData(typeof(IConflictLog))]
    [InlineData(typeof(IManifestSerializer))]
    [InlineData(typeof(IMachineIdentity))]
    [InlineData(typeof(MySync.Application.Abstractions.Updates.IExecutableInstaller))]
    public void AddInfrastructure_Should_ImplementEveryLocalAbstraction(Type abstraction)
    {
        using ServiceProvider provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService(abstraction).ShouldNotBeNull();
    }

    [Fact]
    public void ARegisteredShareRoot_Should_BeUsed_InsteadOfTheCurrentDirectory()
    {
        using ServiceProvider provider = Services().BuildServiceProvider();

        provider.GetRequiredService<ILocalFolder>().RootPath.ShouldBe(new ShareRoot(_temp.Path).FullPath);
    }

    [Fact]
    public void WithoutAShareRoot_Should_UseTheCurrentDirectory()
    {
        using ServiceProvider provider = new ServiceCollection().AddInfrastructure().BuildServiceProvider();

        provider.GetRequiredService<ShareRoot>().FullPath.ShouldBe(Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void MachineIdentity_Should_BeUserAtMachine()
    {
        using ServiceProvider provider = Services().BuildServiceProvider();

        provider.GetRequiredService<IMachineIdentity>().Name.ShouldBe($"{Environment.UserName}@{Environment.MachineName}");
    }

    public void Dispose() => _temp.Dispose();

    private ServiceCollection Services()
    {
        ServiceCollection services = new();
        services.AddSingleton(new ShareRoot(_temp.Path));
        services.AddInfrastructure();
        return services;
    }
}
