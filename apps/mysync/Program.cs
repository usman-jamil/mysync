using System.Globalization;
using ConsoleAppFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySync.Application;
using MySync.Cli;
using MySync.Cli.Commands;
using MySync.Cli.Hosting;
using MySync.Infrastructure;
using MySync.Storage.Azure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// Empty builder on purpose: the default one reads appsettings.json and user secrets from the
// content root, which for a CLI is the folder being synced. Environment variables are the
// only configuration source (CLAUDE.md §5).
HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Configuration.AddEnvironmentVariables();

// Set from --verbose / --quiet once the global options are parsed.
var logLevel = new LoggingLevelSwitch(LogEventLevel.Warning);

// stderr only: stdout carries --json output.
builder.Services.AddSerilog(logger => logger
    .MinimumLevel.ControlledBy(logLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        formatProvider: CultureInfo.InvariantCulture,
        standardErrorFromLevel: LogEventLevel.Verbose));

// ConsoleAppFramework owns Ctrl+C and the process lifetime; the host's banner is noise.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

builder.Services
    .AddApplication()
    .AddInfrastructure();

// An unknown provider registers nothing here; it is reported as ConfigurationErrors.UnknownProvider
// (exit 2) by the first command that needs storage, so --help and --version still work.
if (StorageProviderResolver.Resolve(builder.Configuration) == StorageProvider.AzureBlob)
{
    builder.Services.AddAzureBlobStorage();
}

await using var serviceProviderFactory = new HostServiceProviderFactory(builder);

ConsoleApp.ConsoleAppBuilder app = ConsoleApp.Create()
    .ConfigureContainer(serviceProviderFactory)
    .ConfigureServices((context, _, services) => services.AddSingleton((GlobalOptions)context.GlobalOptions!));

app.ConfigureGlobalOptions((ref ConsoleApp.GlobalOptionsBuilder options) =>
{
    bool verbose = options.AddGlobalOption<bool>("--verbose", "Detailed logging on stderr.");
    bool quiet = options.AddGlobalOption<bool>("--quiet", "Only errors on stderr.");
    bool json = options.AddGlobalOption<bool>("--json", "One machine-readable JSON document on stdout.");
    int? parallel = options.AddGlobalOption<int?>("--parallel", "Maximum concurrent transfers (1-64).");

    logLevel.MinimumLevel = (verbose, quiet) switch
    {
        (true, _) => LogEventLevel.Debug,
        (_, true) => LogEventLevel.Error,
        _ => LogEventLevel.Warning,
    };

    return new GlobalOptions(verbose, quiet, json, parallel);
});

app.Add<DiagnosticsCommands>();

await app.RunAsync(args);
