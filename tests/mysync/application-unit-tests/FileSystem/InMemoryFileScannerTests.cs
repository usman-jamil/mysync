using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.FileSystem;

/// <summary>
/// The fake scanner's output is the local side of every handler test, so its shape is pinned
/// here: real hashes, empty directories only, ignore rules, and the Windows executable-bit rule.
/// </summary>
public class InMemoryFileScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_Should_HashEveryFile_AndRecordItsStat()
    {
        InMemoryLocalFolder folder = new InMemoryLocalFolder().WriteFile("docs/readme.md", "# hi", executable: false);

        ScannedFolder scan = await Scan(folder);

        FileEntry entry = scan.Entries[ManifestBuilder.PathOf("docs/readme.md")].ShouldBeOfType<FileEntry>();
        entry.Sha256.ShouldBe(ManifestBuilder.HashOf("# hi"));
        entry.Size.ShouldBe(4);
        scan.Files[ManifestBuilder.PathOf("docs/readme.md")].Size.ShouldBe(4);
        scan.HashCache[ManifestBuilder.PathOf("docs/readme.md")].Sha256.ShouldBe(entry.Sha256);
    }

    [Fact]
    public async Task Scan_Should_ListOnlyEmptyDirectories()
    {
        InMemoryLocalFolder folder = new InMemoryLocalFolder().AddDirectory("empty").WriteFile("full/a.txt", "a");

        ScannedFolder scan = await Scan(folder);

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["empty", "full/a.txt"]);
        scan.Entries[ManifestBuilder.PathOf("empty")].ShouldBe(DirectoryEntry.Instance);
    }

    [Fact]
    public async Task Scan_Should_LeaveOutIgnoredPaths_AndEverythingUnderAnIgnoredDirectory()
    {
        InMemoryLocalFolder folder = new InMemoryLocalFolder()
            .WriteFile("keep.txt", "k")
            .WriteFile("secret.txt", "s")
            .WriteFile("node_modules/pkg/index.js", "x");

        ScannedFolder scan = await Scan(folder, new PathListIgnoreMatcher("secret.txt", "node_modules"));

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["keep.txt"]);
    }

    [Fact]
    public async Task Scan_Should_ReadTheExecutableBit_OnUnix()
    {
        InMemoryLocalFolder folder = new InMemoryLocalFolder().WriteFile("run.sh", "#!/bin/sh", executable: true);

        ScannedFolder scan = await Scan(folder);

        scan.Entries[ManifestBuilder.PathOf("run.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();
    }

    [Fact]
    public async Task Scan_Should_TakeTheExecutableBitFromTheBase_OnWindows()
    {
        InMemoryLocalFolder folder = new InMemoryLocalFolder { ExecutableBitSupported = false }
            .WriteFile("run.sh", "#!/bin/sh edited on Windows")
            .WriteFile("new.sh", "#!/bin/sh");
        Manifest baseManifest = new ManifestBuilder().WithFile("run.sh", "#!/bin/sh", executable: true).Build();

        ScannedFolder scan = await Scan(folder, baseEntries: baseManifest.Entries);

        scan.Entries[ManifestBuilder.PathOf("run.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();
        scan.Entries[ManifestBuilder.PathOf("new.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeFalse();
    }

    private static async Task<ScannedFolder> Scan(
        InMemoryLocalFolder folder,
        PathListIgnoreMatcher? ignore = null,
        IReadOnlyDictionary<SharePath, ManifestEntry>? baseEntries = null)
    {
        ScanRequest request = new(
            ignore ?? PathListIgnoreMatcher.BuiltInsOnly,
            ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
            baseEntries ?? ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer));

        SharedKernel.Result<ScannedFolder> result = await folder.ScanAsync(request, NullSyncProgressReporter.Instance, Ct);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }
}
