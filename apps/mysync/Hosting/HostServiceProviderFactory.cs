using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MySync.Cli.Hosting;

/// <summary>
/// Lets ConsoleAppFramework build its service provider from our <see cref="HostApplicationBuilder"/>.
/// </summary>
/// <remarks>
/// ConsoleAppFramework builds the provider after it has parsed the global options. With
/// <c>ToConsoleAppBuilder()</c> the host is built first and then replaced by an empty provider
/// as soon as global options are configured. Going through this factory keeps a single
/// container, and anything registered through <c>ConfigureServices</c> (such as the parsed
/// <see cref="GlobalOptions"/>) ends up in the host as well.
/// </remarks>
internal sealed class HostServiceProviderFactory(HostApplicationBuilder builder)
    : IServiceProviderFactory<HostApplicationBuilder>, IAsyncDisposable
{
    private IHost? _host;

    public HostApplicationBuilder CreateBuilder(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            builder.Services.Add(descriptor);
        }

        return builder;
    }

    public IServiceProvider CreateServiceProvider(HostApplicationBuilder containerBuilder)
    {
        _host = containerBuilder.Build();

        return _host.Services;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host?.Dispose();
        }
    }
}
