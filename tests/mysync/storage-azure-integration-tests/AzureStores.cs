using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Storage;
using MySync.Infrastructure;
using MySync.Testing;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>Builds <see cref="AzureBlobShareStore"/>s against the shared Azurite, each on its own container.</summary>
internal static class AzureStores
{
    private static readonly IManifestSerializer Serializer =
        new ServiceCollection().AddInfrastructure().BuildServiceProvider().GetRequiredService<IManifestSerializer>();

    /// <summary>Small blocks, so a few-MB file is several blocks; few retries, so failures are quick.</summary>
    public static AzureTransferSettings TestSettings { get; } = new(1024 * 1024, 1, TimeSpan.FromSeconds(10));

    public static AzureBlobShareStore Create(
        string connectionString, string? container = null, AzureTransferSettings? settings = null, ILogger? logger = null) =>
        new(
            Options.Create(new AzureStorageOptions { ConnectionString = connectionString, Container = container ?? AzuriteEnvironment.NewContainerName() }),
            Serializer,
            settings ?? TestSettings,
            logger);

    public static BlobContainerClient Container(string connectionString, AzureBlobShareStore store) =>
        new(connectionString, store.GetLocation().Value.Container);
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
