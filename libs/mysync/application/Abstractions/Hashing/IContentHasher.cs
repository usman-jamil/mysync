using MySync.Domain.Content;

namespace MySync.Application.Abstractions.Hashing;

/// <summary>SHA-256 of file contents (CLAUDE.md §6). Streams with a buffer of at least 81920 bytes.</summary>
public interface IContentHasher
{
    Task<ContentHash> HashAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>For hashing while streaming, e.g. verifying a download as it is written.</summary>
    IIncrementalContentHash CreateIncremental();
}

public interface IIncrementalContentHash : IDisposable
{
    void Append(ReadOnlySpan<byte> data);

    ContentHash Finish();
}
