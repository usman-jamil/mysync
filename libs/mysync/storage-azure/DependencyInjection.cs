using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;

namespace MySync.Storage.Azure;

public static class DependencyInjection
{
    /// <summary>
    /// The Azure Blob provider, selected by the composition root when
    /// <c>MYSYNC_PROVIDER</c> is <c>azure-blob</c> (the default). Needs an
    /// <see cref="IManifestSerializer"/> (Infrastructure's), so every provider writes the same JSON.
    /// </summary>
    public static IServiceCollection AddAzureBlobStorage(this IServiceCollection services)
    {
        services
            .AddOptions<AzureStorageOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.ConnectionString = configuration[AzureStorageOptions.ConnectionStringVariable];

                string? container = configuration[AzureStorageOptions.ContainerVariable];

                if (!string.IsNullOrWhiteSpace(container))
                {
                    options.Container = container;
                }
            });

        services.TryAddSingleton<IShareStore, AzureBlobShareStore>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// <c>mysync publish</c>'s target: the static website of the account in
    /// <c>MYSYNC_PUBLISH_CONNECTION_STRING</c>, public at <c>MYSYNC_PUBLISH_BASE_URL</c>. Registered
    /// always; the variables are read only by the commands that publish.
    /// </summary>
    public static IServiceCollection AddAzureStaticWebsitePublishing(this IServiceCollection services)
    {
        services
            .AddOptions<PublishStorageOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.ConnectionString = configuration[PublishStorageOptions.ConnectionStringVariable];
                options.BaseUrl = configuration[PublishStorageOptions.BaseUrlVariable];
            });

        services.TryAddSingleton<IPublishTarget, AzureStaticWebsitePublishTarget>();

        return services;
    }
}
