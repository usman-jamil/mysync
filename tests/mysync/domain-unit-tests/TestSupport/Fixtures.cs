using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Domain.UnitTests.TestSupport;

/// <summary>Short constructors for valid domain values, so tests read as data.</summary>
internal static class Fixtures
{
    public const string ShareIdText = "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10";

    // SHA-256 of "test" and of "" (well-known values).
    public const string HashOfTest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    public const string HashOfEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static readonly DateTimeOffset Created = new(2026, 11, 15, 18, 40, 12, TimeSpan.Zero);
    public static readonly DateTimeOffset Updated = new(2026, 11, 16, 9, 2, 55, TimeSpan.Zero);

    public static ShareId Share() => ShareId.TryParse(ShareIdText, out ShareId? id) ? id : throw new InvalidOperationException();

    public static SharePath Path(string value) => Ok(SharePath.Create(value));

    public static ContentHash Hash(string hex) => ContentHash.TryParse(hex, out ContentHash? hash) ? hash : throw new ArgumentException(hex);

    public static Revision Rev(long value) => Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentException(null, nameof(value));

    public static FileEntry File(string hex = HashOfTest, long size = 4, bool executable = false) =>
        Ok(FileEntry.Create(Hash(hex), size, Created, executable));

    public static KeyValuePair<SharePath, ManifestEntry> Entry(string path, ManifestEntry entry) => new(Path(path), entry);

    public static T Ok<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Expected success, got {result.Error.Code}: {result.Error.Description}");
        }

        return result.Value;
    }
}
