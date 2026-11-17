using System.Security.Cryptography;
using MySync.Application.Abstractions.Hashing;
using MySync.Domain.Content;

namespace MySync.Testing;

/// <summary>SHA-256 via the BCL, for handler tests (the real hasher lives in Infrastructure).</summary>
public sealed class TestContentHasher : IContentHasher
{
    public async Task<ContentHash> HashAsync(Stream content, CancellationToken cancellationToken) =>
        ContentHash.FromBytes(await SHA256.HashDataAsync(content, cancellationToken));

    public IIncrementalContentHash CreateIncremental() => new Incremental();

    private sealed class Incremental : IIncrementalContentHash
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public ContentHash Finish() => ContentHash.FromBytes(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }
}
