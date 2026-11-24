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
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Configuration;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
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
    private readonly IPublicationRegistrySerializer _registrySerializer;
    private readonly TimeProvider _timeProvider;
    private readonly AzureTransferSettings _settings;
    private readonly ILogger _logger;
    private readonly Lazy<Result<Connection>> _connection;

    public AzureBlobShareStore(
        IOptions<AzureStorageOptions> options,
        IManifestSerializer serializer,
        IPublicationRegistrySerializer registrySerializer,
        TimeProvider timeProvider,
        ILogger<AzureBlobShareStore>? logger = null)
        : this(options, serializer, registrySerializer, timeProvider, AzureTransferSettings.Default, logger)
    {
    }

    internal AzureBlobShareStore(
        IOptions<AzureStorageOptions> options,
        IManifestSerializer serializer,
        IPublicationRegistrySerializer registrySerializer,
        TimeProvider timeProvider,
        AzureTransferSettings settings,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registrySerializer = registrySerializer ?? throw new ArgumentNullException(nameof(registrySerializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

    /// <remarks>
    /// Blocks are staged as they are read (uncommitted blocks are invisible, and Azure drops them
    /// after about a week), so the stored length needn't be known up front and nothing is buffered
    /// beyond one block. Only after the last block: the producer's verdict, then, for raw bytes, our
    /// own SHA-256 and length check; then the block list is committed with <c>If-None-Match: *</c>.
    /// </remarks>
    public Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, ObjectUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(upload);
        Stream content = upload.Content;
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

            // The staged blocks are never committed unless both checks pass.
            Result verdict = upload.Verify();
            if (verdict.IsFailure)
            {
                return verdict;
            }

            var actual = ContentHash.FromBytes(sha256.GetHashAndReset());
            if (upload.Encoding == ObjectEncoding.Identity && (actual != hash || total != upload.Size))
            {
                AzureLog.HashMismatch(_logger, name, actual.Value);
                return Result.Failure(StorageErrors.HashMismatch(name));
            }

            Dictionary<string, string> metadata = new()
            {
                ["sha256"] = hash.Value,
                ["size"] = upload.Size.ToString(CultureInfo.InvariantCulture),
            };
            if (upload.Encoding != ObjectEncoding.Identity)
            {
                metadata["storedsize"] = total.ToString(CultureInfo.InvariantCulture);
            }

            try
            {
                await blob.CommitBlockListAsync(
                    blocks,
                    new CommitBlockListOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = new BlobHttpHeaders { ContentType = ObjectContentType, ContentEncoding = upload.Encoding.ToContentEncoding() },
                        Metadata = metadata,
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

    /// <remarks>
    /// Returns the <b>stored</b> bytes. The SDK doesn't decode <c>Content-Encoding: gzip</c>: a GET
    /// answers with that header, and the client still hands back the compressed bytes
    /// (<c>AGzipObject_Should_CarryContentEncoding…</c>); <c>OpenReadAsync</c> reads byte ranges of
    /// the blob as stored (<c>AGzipObject_Should_ReadBackAsStored_NotDecoded</c> in the contract suite).
    /// Decoding happens once, in the application.
    /// </remarks>
    public Task<Result<StoredObject>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync(name, async container =>
        {
            BlobClient blob = container.GetBlobClient(name);
            BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            Result<ObjectEncoding> encoding = ObjectEncodings.FromContentEncoding(properties.ContentEncoding);
            if (encoding.IsFailure)
            {
                return Result.Failure<StoredObject>(encoding.Error);
            }

            Stream stream = await blob.OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false) { BufferSize = _settings.BlockSize },
                cancellationToken);
            return Result.Success(new StoredObject(new AzureReadStream(stream, name, _logger), encoding.Value, properties.ContentLength));
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
                Result<ObjectEncoding> encoding = ObjectEncodings.FromContentEncoding(item.Value.Properties.ContentEncoding);
                if (encoding.IsFailure)
                {
                    yield return Result.Failure<StoredObjectInfo>(encoding.Error);
                    yield break;
                }

                yield return new StoredObjectInfo(
                    hash,
                    item.Value.Properties.ContentLength ?? 0,
                    item.Value.Properties.LastModified ?? item.Value.Properties.CreatedOn ?? DateTimeOffset.MinValue,
                    encoding.Value);
            }
        }
    }

    /// <summary>
    /// Soft-deleted blobs and older blob versions under the share's prefix: copies Azure can still
    /// bring back after <c>mysync delete</c>. Listing them needs no extra permission beyond the key.
    /// </summary>
    public Task<Result<int>> CountRecoverableCopiesAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string prefix = ShareLayout.For(shareId).Prefix;

        return CallAsync(prefix, async container =>
        {
            int count = 0;
            try
            {
                await foreach (BlobItem item in container.GetBlobsAsync(BlobTraits.None, BlobStates.Deleted | BlobStates.Version, prefix, cancellationToken))
                {
                    if (item.Deleted || item.IsLatestVersion == false)
                    {
                        count++;
                    }
                }
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return Result.Success(0); // no container: nothing kept
            }

            return Result.Success(count);
        }, cancellationToken);
    }

    public Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).ObjectBlob(hash), cancellationToken);

    /// <summary>
    /// A read-only SAS URL for this one blob, signed with the account key from the connection string.
    /// <c>null</c> when the connection string has no key (e.g. it is itself a SAS): the caller streams.
    /// </summary>
    public Task<Result<CopySource?>> GetCopySourceAsync(ShareId shareId, ContentHash hash, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        return CallAsync<CopySource?>(name, container =>
        {
            BlobClient blob = container.GetBlobClient(name);
            CopySource? source = blob.CanGenerateSasUri ? new CopySource(blob.GenerateSasUri(BlobSasPermissions.Read, _timeProvider.GetUtcNow().Add(lifetime))) : null;
            return Task.FromResult(Result.Success(source));
        }, cancellationToken);
    }

    public Task<Result<PublicationsSnapshot?>> GetPublicationsAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        string name = ShareLayout.For(shareId).Publications;

        return CallAsync<PublicationsSnapshot?>(name, async container =>
        {
            Response<BlobDownloadResult> download;
            try
            {
                download = await container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return Result.Success<PublicationsSnapshot?>(null);
            }

            Result<PublicationRegistry> registry = _registrySerializer.Deserialize(download.Value.Content.ToMemory().Span);
            return registry.IsSuccess
                ? Result.Success<PublicationsSnapshot?>(new PublicationsSnapshot(registry.Value, download.Value.Details.ETag.ToString()))
                : Result.Failure<PublicationsSnapshot?>(registry.Error);
        }, cancellationToken);
    }

    public Task<Result<string>> PutPublicationsAsync(ShareId shareId, PublicationRegistry registry, string? ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        string name = ShareLayout.For(shareId).Publications;

        return CallAsync(name, async container =>
        {
            BlobRequestConditions conditions = ifMatchETag is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(ifMatchETag) };
            try
            {
                Response<BlobContentInfo> written = await container.GetBlobClient(name).UploadAsync(
                    BinaryData.FromBytes(_registrySerializer.Serialize(registry)),
                    new BlobUploadOptions { Conditions = conditions, HttpHeaders = new BlobHttpHeaders { ContentType = ManifestContentType } },
                    cancellationToken);
                return Result.Success(written.Value.ETag.ToString());
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412 or 404)
            {
                // Written by someone else since it was read (or created, or removed): read again.
                return Result.Failure<string>(StorageErrors.PreconditionFailed(name));
            }
        }, cancellationToken);
    }

    public Task<Result> DeletePublicationsAsync(ShareId shareId, CancellationToken cancellationToken) =>
        DeleteAsync(ShareLayout.For(shareId).Publications, cancellationToken);

    private static Result<Connection> Connect(AzureStorageOptions options, AzureTransferSettings settings)
    {
        Result<AzureShareContainer> container = AzureShareContainer.Connect(options, settings);
        return container.IsSuccess ? new Connection(container.Value.Container, container.Value.Location) : Result.Failure<Connection>(container.Error);
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
