using System.Globalization;
using ConsoleAppFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySync.Application;
using MySync.Cli;
using MySync.Cli.Commands;
using MySync.Cli.Hosting;
using MySync.Cli.Rendering;
using MySync.Infrastructure;
using MySync.SiteAssets;
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

// stderr only: stdout carries --json output. While mysync tui has the screen, the log is held in
// memory instead (SwitchableLogSink), so nothing is written over the interface.
var logSink = new SwitchableLogSink(new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Console(
        formatProvider: CultureInfo.InvariantCulture,
        standardErrorFromLevel: LogEventLevel.Verbose)
    .CreateLogger());
builder.Services.AddSingleton(logSink);
builder.Services.AddSerilog(logger => logger
    .MinimumLevel.ControlledBy(logLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Sink(logSink));

// ConsoleAppFramework owns Ctrl+C and the process lifetime; the host's banner is noise.
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

ConsoleOutput.Configure();

// What a previous update may have left next to the executable (Windows keeps the old .exe until now).
MySync.Infrastructure.Updates.UpdateLeftovers.RemoveNextTo(Environment.ProcessPath);

// A typo ("mysync snyc") would otherwise print the help and exit 0.
if (KnownCommands.FindUnknown(args) is { } unknown)
{
    await Console.Error.WriteLineAsync($"'{unknown}' is not a mysync command. Run 'mysync --help' to see them.");
    return ExitCodes.UsageOrConfiguration;
}

// MYSYNC_PROVIDER and MYSYNC_PARALLELISM, parsed once; a bad value is reported by the first
// command that needs it, not here (CLAUDE.md §5).
var environment = new CliEnvironment(builder.Configuration);
builder.Services.AddSingleton(environment);
builder.Services.AddSingleton(environment.SyncOptions);
builder.Services.AddTransient<CommandRunner>();

// mysync clone runs the clone in the folder it picked: this same composition, built again with the
// share root set there (FolderCloneRunner). The current directory never changes.
builder.Services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(provider => new FolderCloneRunner(builder.Services, provider));

builder.Services
    .AddApplication()
    .AddInfrastructure()
    .AddSiteAssets();

// An unknown provider registers nothing here; it is reported as ConfigurationErrors.UnknownProvider
// (exit 2) by the first command that needs storage, so --help and --version still work.
ReleaseSourceSelector.Register(
    builder.Services,
    builder.Configuration,
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

if (environment.Provider == StorageProvider.AzureBlob)
{
    builder.Services.AddAzureBlobStorage().AddAzureStaticWebsitePublishing();
}

// ConsoleAppFramework writes its own messages (unknown option, missing argument) to stdout by
// default; they are errors, and stdout belongs to output and --json.
// Every command catches its own exceptions (CommandRunner), so anything ConsoleAppFramework
// reports itself is a usage error: exit 2, not its default 1 (CLAUDE.md §10).
bool usageError = false;
ConsoleApp.LogError = message =>
{
    usageError = true;
    Console.Error.WriteLine(message);
};

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

app.Add<SyncCommands>();
app.Add<ConflictCommands>();
app.Add<ShareCommands>();
app.Add<MaintenanceCommands>();
app.Add<DiagnosticsCommands>();
app.Add<PublishCommands>();
app.Add<PushCommands>();
app.Add<PullCommands>();
app.Add<TuiCommands>();
app.Add<CloneCommands>();

await app.RunAsync(KnownCommands.ArrangeForParsing(args));

return usageError && Environment.ExitCode == ExitCodes.Unexpected
    ? ExitCodes.UsageOrConfiguration
    : Environment.ExitCode;
