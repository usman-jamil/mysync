using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;

namespace MySync.Storage.S3;

public static class DependencyInjection
{
    /// <summary>
    /// The S3 provider, selected by the composition root when <c>MYSYNC_PROVIDER</c> is <c>s3</c>:
    /// the share bucket <c>MYSYNC_S3_BUCKET</c> in <c>MYSYNC_S3_REGION</c> (or <c>AWS_REGION</c>),
    /// credentials from the standard AWS chain. Needs an <see cref="IManifestSerializer"/>
    /// (Infrastructure's), so every provider writes the same JSON.
    /// </summary>
    public static IServiceCollection AddS3Storage(this IServiceCollection services)
    {
        services
            .AddOptions<S3StorageOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Bucket = configuration[S3StorageOptions.BucketVariable];
                options.Region = FirstSet(configuration[S3StorageOptions.RegionVariable], configuration[S3StorageOptions.AwsRegionVariable]);
            });

        services.TryAddSingleton<S3ShareBucket>();
        services.TryAddSingleton<IShareStore, S3ShareStore>();
        services.TryAddSingleton<IAliasStore, S3AliasStore>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// <c>mysync publish</c>'s target when <c>MYSYNC_PUBLISH_PROVIDER</c> is <c>s3</c>: the public
    /// bucket <c>MYSYNC_PUBLISH_S3_BUCKET</c>, served at <c>MYSYNC_PUBLISH_BASE_URL</c>. Registered
    /// always for that provider; the variables are read only by the commands that publish.
    /// </summary>
    public static IServiceCollection AddS3Publishing(this IServiceCollection services)
    {
        services
            .AddOptions<S3PublishOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Bucket = configuration[S3PublishOptions.BucketVariable];
                options.Region = FirstSet(
                    configuration[S3PublishOptions.RegionVariable],
                    configuration[S3StorageOptions.RegionVariable],
                    configuration[S3StorageOptions.AwsRegionVariable]);
                options.Profile = configuration[S3PublishOptions.ProfileVariable];
                options.BaseUrl = configuration[S3PublishOptions.BaseUrlVariable];
            });

        services.TryAddSingleton<IPublishTarget, S3PublishTarget>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    private static string? FirstSet(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

/// <summary>The share bucket's one connection (and credentials), shared by the share store and the alias store.</summary>
internal sealed class S3ShareBucket : IDisposable
{
    public S3ShareBucket(IOptions<S3StorageOptions> options, ILogger<S3ShareBucket>? logger = null)
        : this(options, S3TransferSettings.Default, logger)
    {
    }

    internal S3ShareBucket(IOptions<S3StorageOptions> options, S3TransferSettings settings, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        S3StorageOptions values = options.Value;
        Connection = new S3Connection(
            () => S3Bucket.From(values.Bucket, values.Region, values.KeyPrefix, S3StorageOptions.BucketVariable, $"{S3StorageOptions.RegionVariable} (or {S3StorageOptions.AwsRegionVariable})"),
            profile: null,
            settings,
            logger);
    }

    internal S3ShareBucket(S3Connection connection) => Connection = connection;

    internal S3Connection Connection { get; }

    public void Dispose() => Connection.Dispose();
}
