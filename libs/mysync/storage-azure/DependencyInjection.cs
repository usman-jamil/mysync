using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MySync.Storage.Azure;

public static class DependencyInjection
{
    /// <summary>
    /// The Azure Blob provider, selected by the composition root when
    /// <c>MYSYNC_PROVIDER</c> is <c>azure-blob</c> (the default).
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

        return services;
    }
}
