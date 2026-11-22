using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// Stored objects are decoded in exactly one place (CLAUDE.md §9, §11): <c>ObjectCodec</c>. A reader
/// that opened an object itself would see gzip bytes for a compressed share, or decode them twice.
/// </summary>
public sealed class OneDecoderTests
{
    private static readonly string[] Allowed =
    [
        Path.Combine("libs", "mysync", "application", "Abstractions", "Storage", "IShareStore.cs"),
        Path.Combine("libs", "mysync", "application", "Sync", "Pipeline", "ObjectCodec.cs"),
    ];

    [Fact]
    public void OnlyTheCodec_Should_OpenStoredObjects()
    {
        string root = RepositoryRoot();
        string[] offenders = [.. new[] { Path.Combine(root, "libs", "mysync", "application"), Path.Combine(root, "apps", "mysync") }
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !Allowed.Any(allowed => file.EndsWith(allowed, StringComparison.Ordinal)))
            .Where(file => File.ReadAllText(file).Contains(".OpenObjectReadAsync(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))];

        offenders.ShouldBeEmpty("read objects through ObjectCodec.OpenDecodedAsync instead");
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "mysync.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("mysync.slnx not found above the test output");
    }
}
