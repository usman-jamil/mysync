using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySync.Infrastructure.Time;
using SharedKernel;

namespace MySync.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Everything on the local machine: disk, state file, hashing, ignore rules, clock.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IDateTimeProvider, DateTimeProvider>();

        return services;
    }
}
