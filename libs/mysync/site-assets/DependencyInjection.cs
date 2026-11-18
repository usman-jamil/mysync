using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySync.Application.Abstractions.Publishing;

namespace MySync.SiteAssets;

public static class DependencyInjection
{
    /// <summary>Registers the publication page assets embedded in this binary.</summary>
    public static IServiceCollection AddSiteAssets(this IServiceCollection services)
    {
        services.TryAddSingleton<ISiteAssetSource, EmbeddedSiteAssetSource>();
        return services;
    }
}
