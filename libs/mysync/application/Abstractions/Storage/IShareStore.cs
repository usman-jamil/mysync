using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Abstractions.Storage;

/// <summary>
/// Where shares live (CLAUDE.md §11). One implementation per provider; the behaviour every
/// implementation must have is pinned by <c>ShareStoreContractTests</c> in MySync.Testing, run
/// against the in-memory fake and against each real provider.
/// </summary>
/// <remarks>
/// Every method returns a <see cref="Result"/>: SDK failures are translated into
/// <c>StorageErrors</c>. Only <see cref="OperationCanceledException"/> from the caller's own token
/// propagates.
/// </remarks>
public interface IShareStore
{
    /// <summary>Provider, account and container, recorded in <c>.mysynctool</c>. Never a secret.</summary>
    Result<StorageLocation> GetLocation();

    /// <summary>Makes the store usable for a new share (e.g. creates the container). Idempotent.</summary>
    Task<Result> EnsureReadyForNewShareAsync(CancellationToken cancellationToken);

    /// <summary>HEAD with its ETag, or a <c>null</c> value if the share doesn't exist (or the store isn't set up yet).</summary>
    Task<Result<ManifestSnapshot?>> GetManifestAsync(ShareId shareId, CancellationToken cancellationToken);

    /// <summary>Writes HEAD for a new share only if none exists (<c>If-None-Match: *</c>); else <c>Storage.AlreadyExists</c>. Returns the ETag.</summary>
    Task<Result<string>> CreateManifestAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces HEAD only if its ETag is still <paramref name="ifMatchETag"/>; else
    /// <c>Storage.PreconditionFailed</c>, also when HEAD no longer exists. Returns the new ETag.
    /// </summary>
    Task<Result<string>> ReplaceManifestAsync(ShareId shareId, Manifest manifest, string ifMatchETag, CancellationToken cancellationToken);

    /// <summary>Writes <c>manifests/&lt;revision&gt;.json</c>. A revision that is already stored counts as success.</summary>
    Task<Result> PutManifestHistoryAsync(ShareId shareId, Manifest manifest, CancellationToken cancellationToken);

    /// <summary>Every history manifest, oldest revision first. An unreadable one is a failed item; listing continues.</summary>
    IAsyncEnumerable<Result<Manifest>> ListManifestHistoryAsync(ShareId shareId, CancellationToken cancellationToken);

    /// <summary>Deletes one history manifest (gc). A missing one counts as success.</summary>
    Task<Result> DeleteManifestHistoryAsync(ShareId shareId, Revision revision, CancellationToken cancellationToken);

    Task<Result<bool>> ObjectExistsAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an object under its hash (<c>If-None-Match: *</c>; an existing object counts as
    /// success and is left as it is). The store <b>verifies</b> that the bytes hash to
    /// <paramref name="hash"/> and stores nothing if they don't (<c>Storage.HashMismatch</c>): a file
    /// that changed while it was read must never become an object under the wrong name, because
    /// objects are immutable and an existing one is never re-uploaded.
    /// </summary>
    /// <param name="content">Must be seekable and report its length, so the SDK can retry a block.</param>
    Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, Stream content, long length, CancellationToken cancellationToken);

    /// <summary>The object's bytes; <c>Storage.NotFound</c> if it doesn't exist. The caller verifies the hash while reading.</summary>
    Task<Result<Stream>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken);

    /// <summary>Every object of the share (gc). Blobs whose name isn't a canonical object name are not listed.</summary>
    IAsyncEnumerable<Result<StoredObjectInfo>> ListObjectsAsync(ShareId shareId, CancellationToken cancellationToken);

    /// <summary>Deletes one object (gc). A missing one counts as success.</summary>
    /// <summary>
    /// After <c>mysync delete</c>: how many deleted blobs of this share the provider can still bring
    /// back (Azure soft delete, blob versions). 0 when it keeps none. The data is only really gone
    /// once these expire, so the command warns about them.
    /// </summary>
    Task<Result<int>> CountRecoverableCopiesAsync(ShareId shareId, CancellationToken cancellationToken);

    Task<Result> DeleteObjectAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken);
}

/// <summary>HEAD as read, with the ETag a later <see cref="IShareStore.ReplaceManifestAsync"/> must match.</summary>
public sealed record ManifestSnapshot(Manifest Manifest, string ETag);

/// <summary>One stored object, for gc: its grace period is measured from <paramref name="LastModifiedUtc"/>.</summary>
public sealed record StoredObjectInfo(ContentHash Hash, long Size, DateTimeOffset LastModifiedUtc);
