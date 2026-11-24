using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
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
    /// success and is left as it is, whatever form it was stored in). The bytes are stored as given,
    /// with <see cref="ObjectUpload.Encoding"/> as the object's <c>Content-Encoding</c>.
    /// <para>
    /// <b>Nothing is committed under the wrong hash.</b> The store reads <see cref="ObjectUpload.Content"/>
    /// to its end (staging what it reads, invisible to readers), then calls
    /// <see cref="ObjectUpload.Verify"/>, and commits only if that succeeds: whoever produced the bytes
    /// hashed the uncompressed content as it went. For raw bytes the store also checks the SHA-256 and
    /// length itself (<c>Storage.HashMismatch</c>). A file that changed while it was read must never
    /// become an object under the wrong name: objects are immutable, and an existing one is never
    /// uploaded again.
    /// </para>
    /// </summary>
    Task<Result> UploadObjectAsync(ShareId shareId, ContentHash hash, ObjectUpload upload, CancellationToken cancellationToken);

    /// <summary>
    /// The object's <b>stored</b> bytes, how they are encoded, and their length; never decoded here.
    /// <c>Storage.NotFound</c> if it doesn't exist; <c>Storage.UnsupportedEncoding</c> if its
    /// <c>Content-Encoding</c> is one this build can't read. The caller decodes and verifies the hash.
    /// </summary>
    Task<Result<StoredObject>> OpenObjectReadAsync(ShareId shareId, ContentHash hash, CancellationToken cancellationToken);

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

    /// <summary>
    /// Where a server-side copy into a publication can read the object from, valid for about
    /// <paramref name="lifetime"/> (Azure: a read-only SAS URL for that one blob; S3: the object
    /// itself for an S3 target, and a presigned GET URL for any other). A <c>null</c> value when this
    /// store can offer neither (e.g. the credential can't sign): the caller then streams the object.
    /// </summary>
    Task<Result<CopySource?>> GetCopySourceAsync(ShareId shareId, ContentHash hash, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>The share's publication records with their ETag, or a <c>null</c> value if it has none yet.</summary>
    Task<Result<PublicationsSnapshot?>> GetPublicationsAsync(ShareId shareId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the records only if they haven't changed since read: <c>If-Match: &lt;ETag&gt;</c>, or
    /// <c>If-None-Match: *</c> when <paramref name="ifMatchETag"/> is <c>null</c> (none existed).
    /// Otherwise <c>Storage.PreconditionFailed</c>. Returns the new ETag.
    /// </summary>
    Task<Result<string>> PutPublicationsAsync(ShareId shareId, PublicationRegistry registry, string? ifMatchETag, CancellationToken cancellationToken);

    /// <summary>Removes the records (<c>mysync delete</c>). Missing counts as success.</summary>
    Task<Result> DeletePublicationsAsync(ShareId shareId, CancellationToken cancellationToken);
}

/// <summary>The publication records as read, with the ETag a later write must match.</summary>
public sealed record PublicationsSnapshot(PublicationRegistry Registry, string ETag);

/// <summary>HEAD as read, with the ETag a later <see cref="IShareStore.ReplaceManifestAsync"/> must match.</summary>
public sealed record ManifestSnapshot(Manifest Manifest, string ETag);

/// <summary>One stored object, for gc: its grace period is measured from <paramref name="LastModifiedUtc"/>.</summary>
/// <param name="Size">The <b>stored</b> length (compressed, for a gzip object).</param>
/// <param name="Encoding">How the object is stored, from the listing: no download needed.</param>
public sealed record StoredObjectInfo(ContentHash Hash, long Size, DateTimeOffset LastModifiedUtc, ObjectEncoding Encoding = ObjectEncoding.Identity);
