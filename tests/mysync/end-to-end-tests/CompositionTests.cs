using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Diagnostics.SelfCheck;
using MySync.Infrastructure;
using MySync.SiteAssets;
using MySync.Storage.Azure;
using MySync.Updates.GitHub;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// The full DI graph, wired the way the CLI wires it. Two-machine sync against Azurite is in
/// <see cref="AzureSyncTests"/>.
/// </summary>
public class CompositionTests
{
    [Fact]
    public void FullGraph_Should_ConstructEveryService()
    {
        using ServiceProvider provider = BuildServices(new Dictionary<string, string?>()).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.ShouldNotBeNull();
    }

    [Fact]
    public async Task FullGraph_Should_ResolveAndRunAHandlerThroughTheDecorators()
    {
        using ServiceProvider provider = BuildServices(new Dictionary<string, string?>()).BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        IQueryHandler<RunSelfCheckQuery, SelfCheckResponse> handler =
            scope.ServiceProvider.GetRequiredService<IQueryHandler<RunSelfCheckQuery, SelfCheckResponse>>();

        Result<SelfCheckResponse> result = await handler.Handle(new RunSelfCheckQuery(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AzureOptions_Should_BindFromEnvironmentStyleKeys()
    {
        using ServiceProvider provider = BuildServices(new Dictionary<string, string?>
        {
            [AzureStorageOptions.ConnectionStringVariable] = "UseDevelopmentStorage=true",
            [AzureStorageOptions.ContainerVariable] = "team-share",
        }).BuildServiceProvider();

        AzureStorageOptions options = provider.GetRequiredService<IOptions<AzureStorageOptions>>().Value;

        options.ConnectionString.ShouldBe("UseDevelopmentStorage=true");
        options.Container.ShouldBe("team-share");
        options.ToString().ShouldNotContain("UseDevelopmentStorage");
    }

    // An in-memory configuration stands in for environment variables, so the developer's real
    // AZURE_STORAGE_CONNECTION_STRING is never read.
    private static ServiceCollection BuildServices(Dictionary<string, string?> environment)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(environment).Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddApplication().AddInfrastructure().AddAzureBlobStorage().AddAzureStaticWebsitePublishing().AddGitHubReleaseSource("acme/mysync", "1.0.0").AddSiteAssets();

        return services;
    }
}
