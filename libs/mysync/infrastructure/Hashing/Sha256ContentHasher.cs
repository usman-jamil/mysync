using System.Buffers;
using System.Security.Cryptography;
using MySync.Application.Abstractions.Hashing;
using MySync.Domain.Content;

namespace MySync.Infrastructure.Hashing;

/// <summary>SHA-256 of file contents (CLAUDE.md §6), streamed through a pooled buffer.</summary>
internal sealed class Sha256ContentHasher : IContentHasher
{
    // CLAUDE.md §12 asks for at least 81920 bytes.
    internal const int BufferSize = 128 * 1024;

    public async Task<ContentHash> HashAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ContentHash.FromBytes(hash.GetHashAndReset());
    }

    public IIncrementalContentHash CreateIncremental() => new Incremental();

    private sealed class Incremental : IIncrementalContentHash
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public ContentHash Finish() => ContentHash.FromBytes(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }
}
