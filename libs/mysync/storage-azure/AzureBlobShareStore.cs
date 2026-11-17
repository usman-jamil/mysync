using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Configuration;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Storage.Azure;

/// <summary>
/// A share in an Azure Blob container, laid out by <see cref="ShareLayout"/> (CLAUDE.md §6, §11).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Commit point:</b> <c>manifest.json</c> is written with <c>If-None-Match: *</c> (new share)
/// or <c>If-Match: &lt;ETag&gt;</c> (existing share), so a concurrent commit gives
/// <see cref="StorageErrors.PreconditionFailed"/> and the pipeline re-plans.</item>
/// <item><b>Objects are verified before they exist:</b> Azure doesn't check SHA-256, so the bytes
/// are staged as uncommitted blocks while being hashed, and the block list is committed (with
/// <c>If-None-Match: *</c>) only if the hash is the object's name. On a mismatch nothing becomes
/// visible; Azure discards uncommitted blocks on its own after about a week. Block IDs are unique
/// per upload, so two machines uploading the same object at once can't mix their blocks.</item>
/// <item><b>Configuration</b> is read on first use, not at startup: a missing or malformed setting
/// is a <see cref="ConfigurationErrors"/> result from whichever call needs storage.</item>
/// <item><b>Secrets:</b> no error carries SDK text, and the debug log gets it redacted.</item>
/// </list>
/// </remarks>
internal sealed partial class AzureBlobShareStore : IShareStore
{
    public const string ProviderName = "azure-blob";

    private const string ManifestContentType = "application/json";
    private const string ObjectContentType = "application/octet-stream";

    private readonly IManifestSerializer _serializer;
    private readonly AzureTransferSettings _settings;
    private readonly ILogger _logger;
    private readonly Lazy<Result<Connection>> _connection;

    public AzureBlobShareStore(IOptions<AzureStorageOptions> options, IManifestSerializer serializer, ILogger<AzureBlobShareStore>? logger = null)
        : this(options, serializer, AzureTransferSettings.Default, logger)
    {
    }

    internal AzureBlobShareStore(IOptions<AzureStorageOptions> options, IManifestSerializer serializer, AzureTransferSettings settings, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        AzureStorageOptions values = options.Value;
        _connection = new Lazy<Result<Connection>>(() => Connect(values, settings));
    }

    public Result<StorageLocation> GetLocation() =>
        _connection.Value.IsSuccess ? _connection.Value.Value.Location : Result.Failure<StorageLocation>(_connection.Value.Error);

    public Task<Result> EnsureReadyForNewShareAsync(CancellationToken cancellationToken) =>
        CallAsync("(container)", async container =>
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<ManifestSnapshot?>> GetManifestAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Manifest;

        return CallAsync<ManifestSnapshot?>(name, async container =>
        {
            Response<BlobDownloadResult> download;
            try
            {
                download = await container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return Result.Success<ManifestSnapshot?>(null); // no such share, or no container yet
            }

            Result<Manifest> manifest = _serializer.Deserialize(download.Value.Content.ToMemory().Span);
            return manifest.IsSuccess
                ? Result.Success<ManifestSnapshot?>(new ManifestSnapshot(manifest.Value, download.Value.Details.ETag.ToString()))
                : Result.Failure<ManifestSnapshot?>(manifest.Error);
        }, cancellationToken);
    }

    public Task<Result<string>> CreateManifestAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Manifest;

        return CallAsync(name, async container =>
        {
            try
            {
                Response<BlobContentInfo> written = await UploadManifestAsync(container, name, manifest, new BlobRequestConditions { IfNoneMatch = ETag.All }, cancellationToken);
                return Result.Success(written.Value.ETag.ToString());
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                return Result.Failure<string>(StorageErrors.AlreadyExists(name));
            }
        }, cancellationToken);
    }

    public Task<Result<string>> ReplaceManifestAsync(ShareId shareId, Manifest manifest, string ifMatchETag, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Manifest;

        return CallAsync(name, async container =>
        {
            try
            {
                Response<BlobContentInfo> written = await UploadManifestAsync(container, name, manifest, new BlobRequestConditions { IfMatch = new ETag(ifMatchETag) }, cancellationToken);
                return Result.Success(written.Value.ETag.ToString());
            }
            catch (RequestFailedException exception) when (exception.Status is 412 or 404)
            {
                // Someone else committed (or HEAD is gone): either way, re-read and re-plan.
                return Result.Failure<string>(StorageErrors.PreconditionFailed(name));
            }
        }, cancellationToken);
    }

    public Task<Result> PutManifestHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string name = ShareLayout.For(shareId).ManifestHistory(manifest.Revision);

        return CallAsync(name, async container =>
        {
            try
            {
                await UploadManifestAsync(container, name, manifest, new BlobRequestConditions { IfNoneMatch = ETag.All }, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                // History is immutable: this revision is already stored.
            }

            return Result.Success();
        }, cancellationToken);
    }

    public async IAsyncEnumerable<Result<Manifest>> ListManifestHistoryAsync(
        ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = ShareLayout.For(shareId);
        List<(Revision Revision, string Name)> found = [];

        await foreach (Result<BlobItem> item in ListAsync(layout.ManifestHistoryPrefix, cancellationToken))
        {
            if (item.IsFailure)
            {
                yield return Result.Failure<Manifest>(item.Error);
                yield break;
            }

            if (layout.TryParseManifestHistory(item.Value.Name, out Revision? revision))
            {
                found.Add((revision, item.Value.Name));
            }
        }

        found.Sort((left, right) => left.Revision.CompareTo(right.Revision));

        foreach ((_, string name) in found)
        {
            yield return await CallAsync(name, async container =>
            {
                Response<BlobDownloadResult> download = await container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
                return _serializer.Deserialize(download.Value.Content.ToMemory().Span);
            }, cancellationToken);
        }
    }

    public Task<Result> DeleteManifestHistoryAsync(ShareId shareId, Revision revision, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ManifestHistory(revision);

        return DeleteAsync(name, cancellationToken);
    }

    public Task<Result<bool>> ObjectExistsAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync(name, async container =>
            Result.Success((bool)await container.GetBlobClient(name).ExistsAsync(cancellationToken)), cancellationToken);
    }

    public Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(content);
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        // Reading `content` may throw IOException (the local file); that is the caller's to handle.
        return CallAsync(name, async container =>
        {
            BlockBlobClient blob = container.GetBlockBlobClient(name);
            if (await blob.ExistsAsync(cancellationToken))
            {
                return Result.Success(); // same name, same bytes
            }

            string uploadId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            List<string> blocks = [];
            long total = 0;
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_settings.BlockSize);
            try
            {
                int read;
                while ((read = await ReadBlockAsync(content, buffer, _settings.BlockSize, cancellationToken)) > 0)
                {
                    sha256.AppendData(buffer, 0, read);
                    string blockId = BlockId(uploadId, blocks.Count);
                    using MemoryStream block = new(buffer, 0, read, writable: false);
                    await blob.StageBlockAsync(blockId, block, cancellationToken: cancellationToken);
                    blocks.Add(blockId);
                    total += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var actual = ContentHash.FromBytes(sha256.GetHashAndReset());
            if (actual != hash || total != length)
            {
                AzureLog.HashMismatch(_logger, name, actual.Value);
                return Result.Failure(StorageErrors.HashMismatch(name)); // the staged blocks are never committed
            }

            try
            {
                await blob.CommitBlockListAsync(
                    blocks,
                    new CommitBlockListOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = new BlobHttpHeaders { ContentType = ObjectContentType },
                        Metadata = new Dictionary<string, string>
                        {
                            ["sha256"] = hash.Value,
                            ["size"] = total.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                // Another upload of the same object won; it has the same bytes.
            }

            return Result.Success();
        }, cancellationToken);
    }

    public Task<Result<Stream>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync(name, async container =>
        {
            Stream stream = await container.GetBlobClient(name).OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false) { BufferSize = _settings.BlockSize },
                cancellationToken);
            return Result.Success<Stream>(new AzureReadStream(stream, name, _logger));
        }, cancellationToken);
    }

    public async IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(
        ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = ShareLayout.For(shareId);

        await foreach (Result<BlobItem> item in ListAsync(layout.ObjectsPrefix, cancellationToken))
        {
            if (item.IsFailure)
            {
                yield return Result.Failure<StoredObjectInfo>(item.Error);
                yield break;
            }

            if (layout.TryParseObject(item.Value.Name, out ContentHash? hash))
            {
                yield return new StoredObjectInfo(
                    hash,
                    item.Value.Properties.ContentLength ?? 0,
                    item.Value.Properties.LastModified ?? item.Value.Properties.CreatedOn ?? DateTimeOffset.MinValue);
            }
        }
    }

    public Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).ObjectBlob(hash), cancellationToken);

    // Azure container names: 3-63 characters, lowercase letters, digits and single hyphens, starting and ending with a letter or digit.
    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContainerName();

    private static Result<Connection> Connect(AzureStorageOptions options, AzureTransferSettings settings)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return Result.Failure<Connection>(ConfigurationErrors.Missing(AzureStorageOptions.ConnectionStringVariable));
        }

        string container = options.Container;
        if (!ContainerName().IsMatch(container))
        {
            return Result.Failure<Connection>(ConfigurationErrors.Invalid(
                AzureStorageOptions.ContainerVariable,
                $"'{container}' is not a container name (3-63 lowercase letters, digits and single hyphens)."));
        }

        BlobClientOptions clientOptions = new()
        {
            Retry =
            {
                Mode = RetryMode.Exponential,
                MaxRetries = settings.MaxRetries,
                Delay = TimeSpan.FromSeconds(0.8),
                MaxDelay = TimeSpan.FromSeconds(30),
                NetworkTimeout = settings.NetworkTimeout,
            },
        };

        try
        {
            BlobServiceClient service = new(options.ConnectionString, clientOptions);
            string account = string.IsNullOrEmpty(service.AccountName) ? service.Uri.Host : service.AccountName;
            return new Connection(service.GetBlobContainerClient(container), new StorageLocation(ProviderName, account, container));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            // The SDK's message may quote the connection string, so it is not used.
            return Result.Failure<Connection>(ConfigurationErrors.Invalid(
                AzureStorageOptions.ConnectionStringVariable, "it is not an Azure Storage connection string."));
        }
    }

    private Task<Response<BlobContentInfo>> UploadManifestAsync(
        BlobContainerClient container, string name, Manifest manifest, BlobRequestConditions conditions, CancellationToken cancellationToken) =>
        container.GetBlobClient(name).UploadAsync(
            BinaryData.FromBytes(_serializer.Serialize(manifest)),
            new BlobUploadOptions { Conditions = conditions, HttpHeaders = new BlobHttpHeaders { ContentType = ManifestContentType } },
            cancellationToken);

    private static string BlockId(string uploadId, int index) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{uploadId}-{index:D6}")); // every ID of one blob must be the same length

    private static async Task<int> ReadBlockAsync(Stream content, byte[] buffer, int size, CancellationToken cancellationToken)
    {
        int filled = 0;
        int read;
        while (filled < size && (read = await content.ReadAsync(buffer.AsMemory(filled, size - filled), cancellationToken)) > 0)
        {
            filled += read;
        }

        return filled;
    }

    private Task<Result> DeleteAsync(string name, CancellationToken cancellationToken) =>
        CallAsync(name, async container =>
        {
            await container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
            return Result.Success();
        }, cancellationToken);

    // A container that doesn't exist yet lists as empty.
    private async IAsyncEnumerable<Result<BlobItem>> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_connection.Value.IsFailure)
        {
            yield return Result.Failure<BlobItem>(_connection.Value.Error);
            yield break;
        }

        await using IAsyncEnumerator<BlobItem> items = _connection.Value.Value.Container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            Result<bool> next = await MoveNextAsync(items, prefix, cancellationToken);
            if (next.IsFailure)
            {
                if (next.Error.Type != ErrorType.NotFound)
                {
                    yield return Result.Failure<BlobItem>(next.Error);
                }

                yield break;
            }

            if (!next.Value)
            {
                yield break;
            }

            yield return items.Current;
        }
    }

    private async Task<Result<bool>> MoveNextAsync(IAsyncEnumerator<BlobItem> items, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            return await items.MoveNextAsync();
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<bool>(Failed(exception, prefix));
        }
    }

    private async Task<Result<T>> CallAsync<T>(string blobName, Func<BlobContainerClient, Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        if (_connection.Value.IsFailure)
        {
            return Result.Failure<T>(_connection.Value.Error);
        }

        try
        {
            return await call(_connection.Value.Value.Container);
        }
        catch (Exception exception) when (AzureErrorMapper.IsStorageFailure(exception, cancellationToken))
        {
            return Result.Failure<T>(Failed(exception, blobName));
        }
    }

    private async Task<Result> CallAsync(string blobName, Func<BlobContainerClient, Task<Result>> call, CancellationToken cancellationToken)
    {
        Result<bool> outcome = await CallAsync(blobName, async container =>
        {
            Result result = await call(container);
            return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
        }, cancellationToken);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);
    }

    private Error Failed(Exception exception, string blobName)
    {
        AzureLog.RequestFailed(_logger, blobName, AzureErrorMapper.StatusOf(exception), ConnectionStringRedactor.Redact(exception.Message));
        return AzureErrorMapper.Map(exception, blobName);
    }

    private sealed record Connection(BlobContainerClient Container, StorageLocation Location);
}
