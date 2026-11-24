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
        ShouldNotDependOn(SharedKernelAssembly, DomainNamespace, ApplicationNamespace, InfrastructureNamespace, StorageNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace);

    [Fact]
    public void Domain_Should_DependOnlyOnSharedKernel() =>
        ShouldNotDependOn(DomainAssembly, ApplicationNamespace, InfrastructureNamespace, StorageNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void Application_Should_NotDependOnOuterLayersOrPresentationPackages() =>
        ShouldNotDependOn(ApplicationAssembly, InfrastructureNamespace, StorageNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, TuiNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void TheTerminalInterface_Should_DependOnlyOnTheApplicationAndInward() =>
        ShouldNotDependOn(TuiAssembly, InfrastructureNamespace, StorageNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void NoLibrary_Should_ReferenceTheTerminalInterface()
    {
        Assembly[] libraries = [SharedKernelAssembly, DomainAssembly, ApplicationAssembly, InfrastructureAssembly, StorageAzureAssembly, StorageS3Assembly, UpdatesGitHubAssembly, SiteAssetsAssembly];

        foreach (Assembly library in libraries)
        {
            library.GetReferencedAssemblies().Select(reference => reference.Name).ShouldNotContain(TuiAssembly.GetName().Name, library.GetName().Name);
        }
    }

    [Fact]
    public void TheApplication_Should_ReferenceNothingTerminalRelated()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("System.Console", TuiNamespace, SpectreConsolePackage)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void TheInterfacesUpdateAndViews_Should_NotTouchTheTerminalOrTheRuntime()
    {
        foreach (string pure in new[] { "MySync.Tui.Update", "MySync.Tui.Views", "MySync.Tui.State", "MySync.Tui.Rendering" })
        {
            TestResult result = Types.InAssembly(TuiAssembly)
                .That().ResideInNamespace(pure)
                .ShouldNot()
                .HaveDependencyOnAny(TuiTerminalNamespace, TuiRuntimeNamespace, "System.Console", "System.IO.File")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue($"{pure}: {Describe(result)}");
        }
    }

    [Fact]
    public void Infrastructure_Should_NotDependOnStorageProvidersOrTheCli() =>
        ShouldNotDependOn(InfrastructureAssembly, StorageNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void StorageProviders_Should_NotDependOnInfrastructureOrTheCli()
    {
        ShouldNotDependOn(StorageAzureAssembly, InfrastructureNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, SpectreConsolePackage, ConsoleAppFrameworkPackage);
        ShouldNotDependOn(StorageS3Assembly, InfrastructureNamespace, UpdatesNamespace, SiteAssetsNamespace, CliNamespace, SpectreConsolePackage, ConsoleAppFrameworkPackage);
    }

    [Fact]
    public void StorageProviders_Should_NeverReferenceEachOther()
    {
        ShouldNotDependOn(StorageAzureAssembly, StorageS3Namespace, AwsPackages);
        ShouldNotDependOn(StorageS3Assembly, StorageAzureNamespace, AzurePackages);
    }

    /// <summary>Only MySync.Storage.Azure references Azure.*, only MySync.Storage.S3 references AWSSDK.*: checked on the assembly references too, not only the types used.</summary>
    [Fact]
    public void EachCloudSdk_Should_BeReferencedByItsStorageProjectOnly()
    {
        Assembly[] libraries = [SharedKernelAssembly, DomainAssembly, ApplicationAssembly, InfrastructureAssembly, StorageAzureAssembly, StorageS3Assembly, UpdatesGitHubAssembly, SiteAssetsAssembly, TuiAssembly, CliAssembly];

        foreach (Assembly library in libraries)
        {
            string[] references = [.. library.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];
            if (library != StorageAzureAssembly)
            {
                references.ShouldNotContain(name => name.StartsWith("Azure.", StringComparison.Ordinal), library.GetName().Name);
            }

            if (library != StorageS3Assembly)
            {
                references.ShouldNotContain(name => name.StartsWith("AWSSDK", StringComparison.Ordinal), library.GetName().Name);
            }
        }
    }

    [Fact]
    public void TheGitHubReleaseSource_Should_DependOnlyOnTheApplication() =>
        ShouldNotDependOn(UpdatesGitHubAssembly, InfrastructureNamespace, StorageNamespace, SiteAssetsNamespace, CliNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void TheSiteAssets_Should_DependOnlyOnTheApplication() =>
        ShouldNotDependOn(SiteAssetsAssembly, InfrastructureNamespace, StorageNamespace, UpdatesNamespace, CliNamespace, AzurePackages, AwsPackages, SpectreConsolePackage, ConsoleAppFrameworkPackage);

    [Fact]
    public void Cli_Should_NotReferenceAzureOrAwsDirectly() =>
        ShouldNotDependOn(CliAssembly, AzurePackages, AwsPackages);

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
