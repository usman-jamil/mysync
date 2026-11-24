using System.Globalization;
using Microsoft.Extensions.Configuration;
using MySync.Application.Sync;
using MySync.Domain.Configuration;
using SharedKernel;

namespace MySync.Cli;

/// <summary>
/// The settings the CLI itself reads from the environment (CLAUDE.md §5), parsed once at startup
/// but reported only by a command that needs them, so <c>--help</c> and <c>--version</c> work
/// with nothing configured.
/// </summary>
internal sealed class CliEnvironment
{
    public const string ParallelismVariable = "MYSYNC_PARALLELISM";

    public const string CompressionLevelVariable = "MYSYNC_COMPRESSION_LEVEL";

    private readonly Error? _problem;
    private readonly Error? _publishProblem;

    public CliEnvironment(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Provider = StorageProviderResolver.Resolve(configuration);
        PublishProvider = StorageProviderResolver.ResolvePublishing(configuration, Provider);
        if (Provider == StorageProvider.Unknown)
        {
            _problem = ConfigurationErrors.UnknownProvider(configuration[StorageProviderResolver.Variable] ?? string.Empty);
        }

        if (PublishProvider == StorageProvider.Unknown)
        {
            _publishProblem = ConfigurationErrors.Invalid(
                StorageProviderResolver.PublishVariable,
                $"'{configuration[StorageProviderResolver.PublishVariable]}' is not a supported publishing provider. Supported: azure-blob, s3.");
        }

        string? parallelism = configuration[ParallelismVariable];
        if (string.IsNullOrWhiteSpace(parallelism))
        {
            SyncOptions = new SyncOptions();
        }
        else if (int.TryParse(parallelism, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value is >= SyncOptions.MinParallelism and <= SyncOptions.MaxAllowedParallelism)
        {
            SyncOptions = new SyncOptions { MaxParallelism = value };
        }
        else
        {
            SyncOptions = new SyncOptions();
            _problem ??= ConfigurationErrors.Invalid(
                ParallelismVariable, $"'{parallelism}' is not a whole number from {SyncOptions.MinParallelism} to {SyncOptions.MaxAllowedParallelism}.");
        }

        // gzip level for uploads to a compressed share: fastest (default) or optimal.
        string? level = configuration[CompressionLevelVariable]?.Trim();
        if (string.Equals(level, "optimal", StringComparison.OrdinalIgnoreCase))
        {
            SyncOptions = SyncOptions with { CompressionLevel = System.IO.Compression.CompressionLevel.Optimal };
        }
        else if (!string.IsNullOrEmpty(level) && !string.Equals(level, "fastest", StringComparison.OrdinalIgnoreCase))
        {
            _problem ??= ConfigurationErrors.Invalid(CompressionLevelVariable, $"'{level}' is not 'fastest' or 'optimal'.");
        }
    }

    public StorageProvider Provider { get; }

    /// <summary>Where publications go: <c>MYSYNC_PUBLISH_PROVIDER</c>, else <see cref="Provider"/>.</summary>
    public StorageProvider PublishProvider { get; }

    public SyncOptions SyncOptions { get; }

    /// <summary>Success, or the first configuration problem a storage command must report.</summary>
    public Result Check() => _problem is null ? Result.Success() : Result.Failure(_problem);

    /// <summary>Success, or the publishing provider's problem (an unknown <c>MYSYNC_PUBLISH_PROVIDER</c>).</summary>
    public Result CheckPublishing() => _publishProblem is null ? Result.Success() : Result.Failure(_publishProblem);
}
