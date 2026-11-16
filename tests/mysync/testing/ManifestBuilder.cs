using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// Builds manifests from real content, so every hash is the SHA-256 of bytes the test also has
/// (<see cref="Contents"/>), ready to upload as objects.
/// </summary>
public sealed class ManifestBuilder(ShareId? shareId = null)
{
    public static readonly DateTimeOffset DefaultTime = new(2026, 11, 15, 18, 40, 12, TimeSpan.Zero);

    private readonly Dictionary<SharePath, ManifestEntry> _entries = [];
    private readonly Dictionary<ContentHash, byte[]> _contents = [];

    public ShareId ShareId { get; } = shareId ?? ShareId.New();

    public ImmutableSortedDictionary<SharePath, ManifestEntry> Entries =>
        ImmutableSortedDictionary.CreateRange(SharePath.OrdinalComparer, _entries);

    /// <summary>The bytes behind every file hash added so far.</summary>
    public IReadOnlyDictionary<ContentHash, byte[]> Contents => _contents;

    public static ContentHash HashOf(byte[] content) => ContentHash.FromBytes(SHA256.HashData(content));

    public static ContentHash HashOf(string content) => HashOf(Encoding.UTF8.GetBytes(content));

    public static SharePath PathOf(string path) => Unwrap(SharePath.Create(path));

    public ManifestBuilder WithFile(string path, string content, bool executable = false, DateTimeOffset? mtimeUtc = null) =>
        WithFile(path, Encoding.UTF8.GetBytes(content), executable, mtimeUtc);

    public ManifestBuilder WithFile(string path, byte[] content, bool executable = false, DateTimeOffset? mtimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ContentHash hash = HashOf(content);
        _contents[hash] = content;
        _entries[PathOf(path)] = Unwrap(FileEntry.Create(hash, content.LongLength, mtimeUtc ?? DefaultTime, executable));
        return this;
    }

    public ManifestBuilder WithDirectory(string path)
    {
        _entries[PathOf(path)] = DirectoryEntry.Instance;
        return this;
    }

    public ManifestBuilder Without(string path)
    {
        _entries.Remove(PathOf(path));
        return this;
    }

    public Manifest Build(long revision = 1, string updatedBy = "test@HOST", DateTimeOffset? updatedUtc = null) =>
        Revision.TryCreate(revision, out Revision? rev)
            ? Unwrap(Manifest.Restore(Manifest.CurrentFormatVersion, ShareId, rev, DefaultTime, updatedUtc ?? DefaultTime, updatedBy, _entries))
            : throw new ArgumentOutOfRangeException(nameof(revision));

    private static T Unwrap<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Description}");
}
