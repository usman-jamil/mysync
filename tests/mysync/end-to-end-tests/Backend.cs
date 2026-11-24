using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using MySync.Domain.State;
using MySync.Storage.Azure;
using MySync.Storage.S3;
using MySync.Testing;
using Xunit;

namespace MySync.EndToEndTests;

/// <summary>
/// The storage a machine uses. The end-to-end flows are written once against this and run once
/// per provider: each place (<see cref="NewPlace"/>) is a fresh, isolated share store, an Azure
/// container or a key prefix in the S3 test bucket.
/// </summary>
public abstract class Backend
{
    public abstract string Provider { get; }

    /// <summary>A fresh, empty, isolated place for one test's shares.</summary>
    public abstract string NewPlace();

    /// <summary>The share settings, named like the environment variables (what a CLI process would get).</summary>
    public abstract Dictionary<string, string?> Settings(string place);

    /// <summary>Settings that point at a different location than <paramref name="place"/> (another container or bucket), for the mismatch checks.</summary>
    public abstract Dictionary<string, string?> Elsewhere(string place);

    /// <summary>The publishing settings; skips (or fails in CI) when this provider has no test publishing side.</summary>
    public abstract Dictionary<string, string?> PublishSettings();

    public abstract void Register(IServiceCollection services, string place);

    public abstract void RegisterPublishing(IServiceCollection services);

    /// <summary>What <c>.mysynctool</c> should record for <paramref name="place"/>.</summary>
    public abstract StorageLocation Location(string place);

    /// <summary>Secrets that must never appear in output or state.</summary>
    public abstract IReadOnlyList<string> Secrets { get; }

    /// <summary>The <c>ShareLayout</c> names stored under <paramref name="prefix"/> in <paramref name="place"/>.</summary>
    public abstract Task<List<string>> ListAsync(string place, string prefix);

    /// <summary>A stored object's <c>Content-Encoding</c> (<c>null</c> when it has none), stored length and <c>size</c> metadata.</summary>
    public abstract Task<StoredProperties> PropertiesAsync(string place, string name);

    /// <summary>The publishing side's public base URL (skips without one).</summary>
    public abstract string PublishBaseUrl { get; }

    /// <summary>The published names under <paramref name="prefix"/> (<c>p/&lt;id&gt;/</c>).</summary>
    public abstract Task<List<string>> ListPublishedAsync(string prefix);

    public override string ToString() => Provider;
}

/// <summary>A container per test in the Azure test account.</summary>
public sealed class AzureBackend(AzureTestStorage storage) : Backend
{
    public override string Provider => "azure-blob";

    public override string NewPlace()
    {
        _ = storage.ConnectionString; // skips (or fails) here without Azure test storage
        return AzureTestStorage.NewContainerName();
    }

    public override Dictionary<string, string?> Settings(string place) => new()
    {
        [AzureStorageOptions.ConnectionStringVariable] = storage.ConnectionString,
        [AzureStorageOptions.ContainerVariable] = place,
    };

    public override Dictionary<string, string?> Elsewhere(string place) => new()
    {
        [AzureStorageOptions.ContainerVariable] = AzureTestStorage.NewContainerName(),
    };

    public override Dictionary<string, string?> PublishSettings() => new()
    {
        [PublishStorageOptions.ConnectionStringVariable] = storage.PublishConnectionString,
        [PublishStorageOptions.BaseUrlVariable] = storage.PublishBaseUrl,
    };

    public override void Register(IServiceCollection services, string place) => services.AddAzureBlobStorage();

    public override void RegisterPublishing(IServiceCollection services) => services.AddAzureStaticWebsitePublishing();

    public override StorageLocation Location(string place) => new("azure-blob", storage.AccountName, place);

    public override IReadOnlyList<string> Secrets => [storage.AccountKey];

    public override async Task<List<string>> ListAsync(string place, string prefix)
    {
        BlobContainerClient container = new(storage.ConnectionString, place);
        List<string> names = [];
        await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, TestContext.Current.CancellationToken))
        {
            names.Add(blob.Name);
        }

        return names;
    }

    public override async Task<StoredProperties> PropertiesAsync(string place, string name)
    {
        BlobClient blob = new BlobContainerClient(storage.ConnectionString, place).GetBlobClient(name);
        BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken)).Value;
        return new StoredProperties(properties.ContentEncoding, properties.ContentLength, properties.Metadata.TryGetValue("size", out string? size) ? size : null);
    }

    public override string PublishBaseUrl => storage.PublishBaseUrl;

    public override async Task<List<string>> ListPublishedAsync(string prefix)
    {
        BlobContainerClient web = new BlobServiceClient(storage.PublishConnectionString).GetBlobContainerClient("$web");
        List<string> names = [];
        await foreach (BlobItem blob in web.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, TestContext.Current.CancellationToken))
        {
            names.Add(blob.Name);
        }

        return names;
    }
}

/// <summary>A key prefix per test in the S3 test bucket (a tests-only store option: the keys under it are <c>ShareLayout</c>'s).</summary>
public sealed class S3Backend(S3TestStorage storage) : Backend
{
    public override string Provider => "s3";

    public override string NewPlace()
    {
        _ = storage.Bucket; // skips (or fails) here without an S3 test bucket
        return S3TestStorage.NewKeyPrefix();
    }

    public override Dictionary<string, string?> Settings(string place) => new()
    {
        ["MYSYNC_PROVIDER"] = "s3",
        [S3StorageOptions.BucketVariable] = storage.Bucket,
        [S3StorageOptions.RegionVariable] = storage.Region,
    };

    public override Dictionary<string, string?> Elsewhere(string place) => new()
    {
        [S3StorageOptions.BucketVariable] = "mysync-not-this-bucket-" + Guid.NewGuid().ToString("N")[..8],
    };

    public override Dictionary<string, string?> PublishSettings() => new()
    {
        [S3PublishOptions.BucketVariable] = storage.PublishBucket,
        [S3PublishOptions.RegionVariable] = storage.Region,
        [S3PublishOptions.BaseUrlVariable] = storage.PublishBaseUrl,
    };

    public override void Register(IServiceCollection services, string place)
    {
        services.AddS3Storage();
        services.PostConfigure<S3StorageOptions>(options => options.KeyPrefix = place);
    }

    public override void RegisterPublishing(IServiceCollection services) => services.AddS3Publishing();

    public override StorageLocation Location(string place) => new("s3", storage.Region, storage.Bucket);

    public override IReadOnlyList<string> Secrets =>
        [.. SecretVariables.Select(Environment.GetEnvironmentVariable).OfType<string>().Where(value => value.Length > 0)];

    private static readonly string[] SecretVariables = ["AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"];

    public override async Task<List<string>> ListAsync(string place, string prefix) =>
        [.. (await storage.ListAsync(place + prefix)).Select(item => item.Key[place.Length..])];

    public override async Task<StoredProperties> PropertiesAsync(string place, string name)
    {
        using AmazonS3Client client = storage.Client();
        GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(storage.Bucket, place + name, TestContext.Current.CancellationToken);
        string? encoding = string.IsNullOrEmpty(head.Headers.ContentEncoding) ? null : head.Headers.ContentEncoding;
        return new StoredProperties(encoding, head.ContentLength, head.Metadata.Keys.Contains("x-amz-meta-size") ? head.Metadata["x-amz-meta-size"] : null);
    }

    public override string PublishBaseUrl => storage.PublishBaseUrl;

    public override async Task<List<string>> ListPublishedAsync(string prefix) =>
        [.. (await storage.ListAsync(prefix, storage.PublishBucket)).Select(item => item.Key)];
}

/// <summary>What a stored object says about itself, whatever the provider.</summary>
/// <param name="Size">The <c>size</c> metadata: the uncompressed length.</param>
public sealed record StoredProperties(string? ContentEncoding, long Length, string? Size);
