using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Diagnostics.SelfCheck;
using MySync.Infrastructure;
using MySync.SiteAssets;
using MySync.Storage.Azure;
using MySync.Storage.S3;
using MySync.Updates.GitHub;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// The full DI graph, wired the way the CLI wires it. Two-machine sync against real storage is in
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

    [Fact]
    public void TheS3Graph_Should_ConstructEveryService_WithoutTouchingCredentials()
    {
        using ServiceProvider provider = BuildServices(new Dictionary<string, string?>(), s3: true).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        MySync.Application.Abstractions.Storage.IShareStore store = provider.GetRequiredService<MySync.Application.Abstractions.Storage.IShareStore>();
        store.GetLocation().Error.Code.ShouldBe("Configuration.Missing"); // no bucket: a configuration error, not a crash
    }

    [Fact]
    public void S3Options_Should_BindFromEnvironmentStyleKeys_WithAwsRegionAsTheFallback()
    {
        using ServiceProvider provider = BuildServices(new Dictionary<string, string?>
        {
            [S3StorageOptions.BucketVariable] = "team-shares",
            [S3StorageOptions.AwsRegionVariable] = "eu-west-1",
            [S3PublishOptions.BucketVariable] = "team-publish",
            [S3PublishOptions.BaseUrlVariable] = "https://team-publish.s3.eu-west-1.amazonaws.com",
            [S3PublishOptions.ProfileVariable] = "publisher",
        }, s3: true).BuildServiceProvider();

        S3StorageOptions shares = provider.GetRequiredService<IOptions<S3StorageOptions>>().Value;
        shares.Bucket.ShouldBe("team-shares");
        shares.Region.ShouldBe("eu-west-1");
        S3PublishOptions publishing = provider.GetRequiredService<IOptions<S3PublishOptions>>().Value;
        publishing.Region.ShouldBe("eu-west-1", "the publishing bucket's region falls back to the share's");
        publishing.Profile.ShouldBe("publisher");
        provider.GetRequiredService<MySync.Application.Abstractions.Storage.IShareStore>().GetLocation().Value
            .ShouldBe(new MySync.Domain.State.StorageLocation("s3", "eu-west-1", "team-shares"));
    }

    // An in-memory configuration stands in for environment variables, so the developer's real
    // AZURE_STORAGE_CONNECTION_STRING (or MYSYNC_S3_BUCKET) is never read.
    private static ServiceCollection BuildServices(Dictionary<string, string?> environment, bool s3 = false)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(environment).Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(provider => new MySync.Cli.Hosting.FolderCloneRunner(services, provider));
        services.AddApplication().AddInfrastructure().AddGitHubReleaseSource("acme/mysync", "1.0.0").AddSiteAssets();
        if (s3)
        {
            services.AddS3Storage().AddS3Publishing();
        }
        else
        {
            services.AddAzureBlobStorage().AddAzureStaticWebsitePublishing();
        }

        return services;
    }
}
