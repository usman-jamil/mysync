using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Storage;
using MySync.Infrastructure;
using MySync.Storage.S3.IntegrationTests;
using MySync.Testing;
using SharedKernel;
using Xunit;

[assembly: AssemblyFixture(typeof(S3StorageFixture))]

namespace MySync.Storage.S3.IntegrationTests;

/// <summary>
/// The real, test-only S3 bucket (CLAUDE.md §14) for the whole assembly; each store gets its own
/// key prefix under this run's. A test that needs it skips without it (fails in CI, where
/// <c>MYSYNC_TEST_REQUIRE_STORAGE</c> is set). Removes what this run wrote at the end.
/// </summary>
public sealed class S3StorageFixture : IAsyncLifetime
{
    public S3TestStorage Storage { get; } = S3TestStorage.FromEnvironment();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await Storage.CleanUpAsync();
}

/// <summary>Builds the S3 stores against the test bucket, each under its own key prefix.</summary>
internal static class S3Stores
{
    private static readonly ServiceProvider Infrastructure = new ServiceCollection().AddInfrastructure().BuildServiceProvider();

    private static readonly ConditionalWeakTable<object, string> Prefixes = [];

    public static IManifestSerializer Serializer { get; } = Infrastructure.GetRequiredService<IManifestSerializer>();

    public static IPublicationRegistrySerializer RegistrySerializer { get; } = Infrastructure.GetRequiredService<IPublicationRegistrySerializer>();

    /// <summary>5 MiB parts (S3's minimum), so an 11 MiB file is a three-part upload; few retries, so failures are quick.</summary>
    public static S3TransferSettings TestSettings { get; } = new(S3Limits.MinPartSize, MaxRetries: 2, TimeSpan.FromSeconds(60));

#pragma warning disable CA2000 // Each store holds its connection for the test's lifetime; the process ends with the run.
    public static S3ShareStore ShareStore(S3TestStorage storage, S3TransferSettings? settings = null, ILogger? logger = null, string? prefix = null)
    {
        string keyPrefix = prefix ?? S3TestStorage.NewKeyPrefix();
        S3ShareStore store = new(Connection(storage, keyPrefix, settings, logger), Serializer, RegistrySerializer, TimeProvider.System, options: null, logger);
        Prefixes.AddOrUpdate(store, keyPrefix);
        return store;
    }

    public static S3AliasStore AliasStore(S3TestStorage storage, int? pageSize = null, string? prefix = null)
    {
        string keyPrefix = prefix ?? S3TestStorage.NewKeyPrefix();
        S3AliasStore store = new(Connection(storage, keyPrefix, settings: null, logger: null), TimeProvider.System, options: null, pageSize, logger: null);
        Prefixes.AddOrUpdate(store, keyPrefix);
        return store;
    }
#pragma warning restore CA2000

    /// <summary>The key prefix a store from here writes under.</summary>
    public static string PrefixOf(object store) => Prefixes.TryGetValue(store, out string? prefix) ? prefix : throw new ArgumentException("Not a store from S3Stores.", nameof(store));

    public static S3Connection Connection(S3TestStorage storage, string keyPrefix, S3TransferSettings? settings, ILogger? logger)
    {
        string bucket = storage.Bucket; // skips (or fails) here, before anything else, without a test bucket
        string region = storage.Region;
        return new S3Connection(
            () => S3Bucket.From(bucket, region, keyPrefix, S3StorageOptions.BucketVariable, S3StorageOptions.RegionVariable),
            profile: null,
            settings ?? TestSettings,
            logger);
    }

    /// <summary>A connection that signs with these (wrong) keys instead of the chain's.</summary>
    public static S3Connection WithCredentials(S3TestStorage storage, string accessKey, string secretKey, ILogger logger)
    {
        string bucket = storage.Bucket;
        string region = storage.Region;
        return new S3Connection(
            () => S3Bucket.From(bucket, region, S3TestStorage.NewKeyPrefix(), S3StorageOptions.BucketVariable, S3StorageOptions.RegionVariable),
            (target, _) => Task.FromResult(Result.Success(new S3Client(
                new AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(target.Region), MaxErrorRetry = 0 }),
                S3Connection.ScopeOf(accessKey)))),
            TestSettings);
    }
}

/// <summary>Keeps every formatted log line, to check none carries a secret.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
