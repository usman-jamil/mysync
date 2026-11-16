using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;
using static MySync.ArchitectureTests.Assemblies;

namespace MySync.ArchitectureTests;

/// <summary>
/// The dependency rules from CLAUDE.md §3. If one of these fails, fix the design, not the test.
/// </summary>
public class LayerTests
{
    [Fact]
    public void SharedKernel_Should_NotDependOnAnyMySyncProject() =>
        ShouldNotDependOn(SharedKernelAssembly, DomainNamespace, ApplicationNamespace, InfrastructureNamespace, StorageNamespace, CliNamespace);

    [Fact]
    public void Domain_Should_DependOnlyOnSharedKernel() =>
        ShouldNotDependOn(DomainAssembly, ApplicationNamespace, InfrastructureNamespace, StorageNamespace, CliNamespace, AzurePackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void Application_Should_NotDependOnOuterLayersOrPresentationPackages() =>
        ShouldNotDependOn(ApplicationAssembly, InfrastructureNamespace, StorageNamespace, CliNamespace, AzurePackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void Infrastructure_Should_NotDependOnStorageProvidersOrTheCli() =>
        ShouldNotDependOn(InfrastructureAssembly, StorageNamespace, CliNamespace, AzurePackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void StorageProviders_Should_NotDependOnInfrastructureOrTheCli() =>
        ShouldNotDependOn(StorageAzureAssembly, InfrastructureNamespace, CliNamespace, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void Cli_Should_NotReferenceAzureDirectly() =>
        ShouldNotDependOn(CliAssembly, AzurePackages);

    [Fact]
    public void Handlers_Should_BeInternalAndSealed()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That().HaveNameEndingWith("Handler", StringComparison.Ordinal)
            .And().AreClasses()
            .And().DoNotResideInNamespace("MySync.Application.Abstractions")
            .Should().NotBePublic()
            .And().BeSealed()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    private static void ShouldNotDependOn(Assembly assembly, params string[] forbidden)
    {
        TestResult result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    private static string Describe(TestResult result) =>
        "Offending types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
