using System.Reflection;
using MySync.Application.Abstractions.Messaging;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.ArchitectureTests;

internal static class Assemblies
{
    public static readonly Assembly SharedKernelAssembly = typeof(Result).Assembly;
    public static readonly Assembly DomainAssembly = typeof(StateFileNames).Assembly;
    public static readonly Assembly ApplicationAssembly = typeof(ICommand).Assembly;
    public static readonly Assembly InfrastructureAssembly = typeof(MySync.Infrastructure.DependencyInjection).Assembly;
    public static readonly Assembly StorageAzureAssembly = typeof(MySync.Storage.Azure.AzureStorageOptions).Assembly;
    public static readonly Assembly CliAssembly = typeof(MySync.Cli.Commands.DiagnosticsCommands).Assembly;

    public const string SharedKernelNamespace = "SharedKernel";
    public const string DomainNamespace = "MySync.Domain";
    public const string ApplicationNamespace = "MySync.Application";
    public const string InfrastructureNamespace = "MySync.Infrastructure";
    public const string StorageNamespace = "MySync.Storage";
    public const string CliNamespace = "MySync.Cli";

    public const string AzurePackages = "Azure";
    public const string SpectreConsolePackage = "Spectre.Console";
    public const string ConsoleAppFrameworkPackage = "ConsoleAppFramework";
}
