using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// An <see cref="IShareStore"/> in memory, with the same conditional-write semantics as Azure
/// (<c>If-Match</c>, <c>If-None-Match: *</c>) and the same blob names (<see cref="ShareLayout"/>).
/// Its behaviour is pinned by <see cref="Contracts.ShareStoreContractTests"/>, which also runs
/// against Azurite: if the two disagree, this fake is wrong. Thread-safe.
/// </summary>
public sealed class InMemoryShareStore(IDateTimeProvider? clock = null, StorageLocation? location = null) : IShareStore
{
    public static readonly StorageLocation DefaultLocation = new("in-memory", "memory", "mysync");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Blob> _blobs = new(StringComparer.Ordinal);
    private long _etagCounter;

    /// <summary>
    /// Runs just before a conditional replace is checked: lets a test commit as "another machine"
    /// between this machine's read and its write.
    /// </summary>
    public Func<ShareId, Task>? BeforeReplaceManifest { get; set; }

    /// <summary>Replaces the stream a download gets, e.g. with a <see cref="FailingStream"/> to simulate a network drop.</summary>
    public Func<ContentHash, byte[], Stream>? DownloadStream { get; set; }

    /// <summary>Runs before objects are listed: lets a test commit as "another machine" while gc is planning.</summary>
    public Func<ShareId, Task>? BeforeListObjects { get; set; }

    /// <summary>History revisions that list as unreadable (a damaged blob), as a real store would report them.</summary>
    public HashSet<long> UnreadableHistory { get; } = [];

    /// <summary>What <see cref="CountRecoverableCopiesAsync"/> reports: simulates an account with soft delete or versioning.</summary>
    public int RecoverableCopies { get; set; }

    /// <summary>Runs before each object delete; return an error to make it fail (an interrupted <c>mysync delete</c>).</summary>
    public Func<ContentHash, Error?>? BeforeDeleteObject { get; set; }

    /// <summary>
    /// Whether <see cref="GetObjectReadUrlAsync"/> gives URLs (like Azure with an account key) or
    /// <c>null</c> (like a SAS-only connection string), so publishing streams instead.
    /// </summary>
    public bool CanMakeReadUrls { get; set; } = true;

    /// <summary>Runs just before a conditional write of <c>publications.json</c>: lets a test write it as "another machine".</summary>
    public Func<ShareId, Task>? BeforePutPublications { get; set; }

    /// <summary>The bytes behind a URL from <see cref="GetObjectReadUrlAsync"/> (what a server-side copy reads), or <c>null</c>.</summary>
    public byte[]? ReadUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != "memory")
        {
            return null;
        }

        lock (_gate)
        {
            return _blobs.TryGetValue(Uri.UnescapeDataString(url.AbsolutePath.TrimStart('/')), out Blob? blob) ? blob.Bytes : null;
        }
    }

    /// <summary>Makes every history write fail (history is best-effort).</summary>
    public bool FailHistoryWrites { get; set; }

    /// <summary>True once <see cref="EnsureReadyForNewShareAsync"/> has run (the create flow must call it).</summary>
    public bool IsReady { get; private set; }

    /// <summary>Every stored blob name, sorted: what a test can see in the "container".</summary>
    public IReadOnlyList<string> BlobNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _blobs.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    public Result<StorageLocation> GetLocation() => location ?? DefaultLocation;

    public Task<Result> EnsureReadyForNewShareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsReady = true;
        return Task.FromResult(Result.Success());
    }

    public Task<Result<ManifestSnapshot?>> GetManifestAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ManifestSnapshot? snapshot = _blobs.TryGetValue(ShareLayout.For(shareId).Manifest, out Blob? blob)
                ? new ManifestSnapshot(blob.Manifest!, blob.ETag)
                : null;

            return Task.FromResult(Result.Success(snapshot));
        }
    }

    public Task<Result<string>> CreateManifestAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ShareLayout.For(shareId).Manifest;

        lock (_gate)
        {
            if (_blobs.ContainsKey(name))
            {
                return Task.FromResult(Result.Failure<string>(StorageErrors.AlreadyExists(name)));
            }

            return Task.FromResult(Result.Success(Put(name, bytes: null, manifest).ETag));
        }
    }

    public async Task<Result<string>> ReplaceManifestAsync(ShareId shareId, Manifest manifest, string ifMatchETag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (BeforeReplaceManifest is { } hook)
        {
            await hook(shareId);
        }

        string name = ShareLayout.For(shareId).Manifest;

        lock (_gate)
        {
            if (!_blobs.TryGetValue(name, out Blob? current) || !string.Equals(current.ETag, ifMatchETag, StringComparison.Ordinal))
            {
                return Result.Failure<string>(StorageErrors.PreconditionFailed(name));
            }

            return Result.Success(Put(name, bytes: null, manifest).ETag);
        }
    }

    public Task<Result> PutManifestHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ShareLayout.For(shareId).ManifestHistory(manifest.Revision);

        if (FailHistoryWrites)
        {
            return Task.FromResult(Result.Failure(StorageErrors.Unreachable));
        }

        lock (_gate)
        {
            if (!_blobs.ContainsKey(name))
            {
                Put(name, bytes: null, manifest);
            }
        }

        return Task.FromResult(Result.Success());
    }

    public async IAsyncEnumerable<Result<Manifest>> ListManifestHistoryAsync(
        ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = ShareLayout.For(shareId);
        List<(Revision Revision, Manifest Manifest)> history = [];

        lock (_gate)
        {
            foreach ((string name, Blob blob) in _blobs)
            {
                if (layout.TryParseManifestHistory(name, out Revision? revision))
                {
                    history.Add((revision, blob.Manifest!));
                }
            }
        }

        history.Sort((x, y) => x.Revision.CompareTo(y.Revision));

        foreach ((Revision revision, Manifest manifest) in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return UnreadableHistory.Contains(revision.Value)
                ? Result.Failure<Manifest>(ShareErrors.InvalidManifest($"history {revision} is damaged"))
                : manifest;
        }
    }

    public Task<Result> DeleteManifestHistoryAsync(ShareId shareId, Revision revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _blobs.Remove(ShareLayout.For(shareId).ManifestHistory(revision));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<bool>> ObjectExistsAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(Result.Success(_blobs.ContainsKey(ShareLayout.For(shareId).ObjectBlob(hash))));
        }
    }

    public async Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        lock (_gate)
        {
            if (_blobs.ContainsKey(name))
            {
                return Result.Success();
            }
        }

        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();

        if (ContentHash.FromBytes(SHA256.HashData(bytes)) != hash)
        {
            return Result.Failure(StorageErrors.HashMismatch(name));
        }

        lock (_gate)
        {
            // A concurrent upload of the same object may have won; same name, same content.
            if (!_blobs.ContainsKey(name))
            {
                Put(name, bytes, manifest: null);
            }
        }

        return Result.Success();
    }

    public Task<Result<Stream>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ShareLayout.For(shareId).ObjectBlob(hash);

        lock (_gate)
        {
            return Task.FromResult(_blobs.TryGetValue(name, out Blob? blob)
                ? Result.Success<Stream>(DownloadStream?.Invoke(hash, blob.Bytes!) ?? new MemoryStream(blob.Bytes!, writable: false))
                : Result.Failure<Stream>(StorageErrors.NotFound(name)));
        }
    }

    public async IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(
        ShareId shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (BeforeListObjects is { } hook)
        {
            await hook(shareId);
        }

        var layout = ShareLayout.For(shareId);
        List<StoredObjectInfo> objects = [];

        lock (_gate)
        {
            foreach ((string name, Blob blob) in _blobs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (layout.TryParseObject(name, out ContentHash? hash))
                {
                    objects.Add(new StoredObjectInfo(hash, blob.Bytes!.LongLength, blob.LastModifiedUtc));
                }
            }
        }

        foreach (StoredObjectInfo info in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return info;
        }
    }

    public Task<Result<int>> CountRecoverableCopiesAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result.Success(RecoverableCopies));
    }

    public Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeDeleteObject?.Invoke(hash) is { } failure)
        {
            return Task.FromResult(Result.Failure(failure));
        }

        lock (_gate)
        {
            _blobs.Remove(ShareLayout.For(shareId).ObjectBlob(hash));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<Uri?>> GetObjectReadUrlAsync(ShareId shareId, ContentHash hash, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri? url = CanMakeReadUrls ? new Uri("memory://store/" + Uri.EscapeDataString(ShareLayout.For(shareId).ObjectBlob(hash))) : null;
        return Task.FromResult(Result.Success(url));
    }

    public Task<Result<PublicationsSnapshot?>> GetPublicationsAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            PublicationsSnapshot? snapshot = _blobs.TryGetValue(ShareLayout.For(shareId).Publications, out Blob? blob)
                ? new PublicationsSnapshot(blob.Registry!, blob.ETag)
                : null;
            return Task.FromResult(Result.Success(snapshot));
        }
    }

    public async Task<Result<string>> PutPublicationsAsync(ShareId shareId, PublicationRegistry registry, string? ifMatchETag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforePutPublications is { } hook)
        {
            await hook(shareId);
        }

        string name = ShareLayout.For(shareId).Publications;
        lock (_gate)
        {
            bool exists = _blobs.TryGetValue(name, out Blob? current);
            bool matches = ifMatchETag is null ? !exists : exists && string.Equals(current!.ETag, ifMatchETag, StringComparison.Ordinal);
            return matches
                ? Result.Success(Put(name, bytes: null, manifest: null, registry).ETag)
                : Result.Failure<string>(StorageErrors.PreconditionFailed(name));
        }
    }

    public Task<Result> DeletePublicationsAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _blobs.Remove(ShareLayout.For(shareId).Publications);
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>Puts a blob that isn't a valid object or manifest name, e.g. to check listings ignore it.</summary>
    public void PutStrayBlob(string name, byte[] bytes)
    {
        lock (_gate)
        {
            Put(name, bytes, manifest: null);
        }
    }

    private Blob Put(string name, byte[]? bytes, Manifest? manifest, PublicationRegistry? registry = null)
    {
        DateTimeOffset now = clock is null ? DateTimeOffset.UtcNow : new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);
        string etag = string.Create(CultureInfo.InvariantCulture, $"\"0x{++_etagCounter:X}\"");
        Blob blob = new(bytes, manifest, etag, now, registry);
        _blobs[name] = blob;
        return blob;
    }

    private sealed record Blob(byte[]? Bytes, Manifest? Manifest, string ETag, DateTimeOffset LastModifiedUtc, PublicationRegistry? Registry = null);
}
