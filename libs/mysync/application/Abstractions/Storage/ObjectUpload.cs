using MySync.Domain.Content;
using SharedKernel;

namespace MySync.Application.Abstractions.Storage;

/// <summary>
/// What <see cref="IShareStore.UploadObjectAsync"/> stores: the bytes, already in their stored form,
/// and how to tell whether they may be committed.
/// </summary>
/// <param name="Content">
/// The bytes to store, read once from start to end. Their length needn't be known in advance and the
/// stream needn't be seekable: a compressed upload streams through without a temporary file.
/// </param>
/// <param name="Encoding">How <paramref name="Content"/> is encoded; becomes the object's <c>Content-Encoding</c>.</param>
/// <param name="Size">The <b>uncompressed</b> size, the object's <c>size</c> metadata (as the manifest says).</param>
/// <param name="Verify">
/// Called once, after <paramref name="Content"/> has returned its last byte and before anything is
/// committed. A failure (e.g. <c>Storage.HashMismatch</c>: the file changed while it was read) means
/// nothing is stored, and the store returns that failure.
/// </param>
public sealed record ObjectUpload(Stream Content, ObjectEncoding Encoding, long Size, Func<Result> Verify)
{
    /// <summary>Raw bytes, checked by the store itself.</summary>
    public static ObjectUpload Raw(Stream content, long size) => new(content, ObjectEncoding.Identity, size, static () => Result.Success());
}

/// <summary>
/// An object as stored: its bytes (not decoded), their encoding and length. Disposing it disposes
/// <see cref="Content"/>.
/// </summary>
public sealed record StoredObject(Stream Content, ObjectEncoding Encoding, long StoredLength) : IAsyncDisposable, IDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}
