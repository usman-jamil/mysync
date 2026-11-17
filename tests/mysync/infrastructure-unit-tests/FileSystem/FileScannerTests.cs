using System.Collections.Immutable;
using System.Runtime.Versioning;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Progress;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.Hashing;
using MySync.Infrastructure.Ignore;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.FileSystem;

/// <summary>The real scanner on real temp folders (CLAUDE.md §8, §9 phase 2, §14 "Infrastructure").</summary>
public sealed class FileScannerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = Now.AddDays(-3);

    private readonly TempShareFolder _temp = new();
    private readonly FixedDateTimeProvider _clock = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_Should_ListFilesAndEmptyDirectories_SortedOrdinally_WithHashSizeAndMtime()
    {
        _temp.WriteFile("b.txt", "bee", LongAgo).WriteFile("A/inner.md", "# a", LongAgo).CreateDirectory("empty").CreateDirectory("parent/child");

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["A/inner.md", "b.txt", "empty", "parent/child"]);
        FileEntry bee = scan.Entries[P("b.txt")].ShouldBeOfType<FileEntry>();
        bee.Sha256.ShouldBe(ManifestBuilder.HashOf("bee"));
        bee.Size.ShouldBe(3);
        bee.MtimeUtc.ShouldBe(new DateTimeOffset(LongAgo));
        scan.Files[P("b.txt")].ShouldBe(new LocalFile(P("b.txt"), 3, new DateTimeOffset(LongAgo)));
        scan.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Scan_Should_SkipIgnoredPaths_AndNotEnterIgnoredDirectories()
    {
        _temp.WriteFile("keep.txt", "k", LongAgo).WriteFile("debug.log", "l", LongAgo).WriteFile("node_modules/x/y.js", "y", LongAgo);
        CountingHasher hasher = new();

        ScannedFolder scan = await ScanAsync(ignore: new GitIgnoreMatcher(["*.log", "node_modules/"]), hasher: hasher);

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["keep.txt"]);
        hasher.Hashed.ShouldBe(["keep.txt"]);
    }

    [Fact]
    public async Task Scan_Should_NeverIncludeMysyncsOwnArtifacts_WhateverTheMatcherSays()
    {
        _temp.WriteFile(StateFileNames.State, "{}").WriteFile(StateFileNames.ConflictLog, "log").WriteFile($"{StateFileNames.StagingDirectory}/x.part", "p").WriteFile("real.txt", "r", LongAgo);

        ScannedFolder scan = await ScanAsync(ignore: new NothingIgnored());

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["real.txt"]);
    }

    [Fact]
    public async Task ADirectoryWhoseEntriesAreAllIgnored_Should_CountAsEmpty()
    {
        _temp.WriteFile("build/out.log", "l").WriteFile("photos/.DS_Store", "finder");

        ScannedFolder scan = await ScanAsync(ignore: new GitIgnoreMatcher(["*.log"]));

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["build", "photos"]);
        scan.Entries.Values.ShouldAllBe(entry => entry is DirectoryEntry);
    }

    [Fact]
    public async Task HashCache_Should_BeReused_WhenSizeAndMtimeMatch_WithoutReadingTheFile()
    {
        _temp.WriteFile("big.bin", "content", LongAgo);
        ContentHash remembered = ManifestBuilder.HashOf("what the cache says");
        CountingHasher hasher = new();

        ScannedFolder scan = await ScanAsync(cache: Cache(("big.bin", 7, LongAgo, remembered)), hasher: hasher);

        scan.Entries[P("big.bin")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(remembered);
        hasher.Hashed.ShouldBeEmpty();
        scan.HashCache[P("big.bin")].Sha256.ShouldBe(remembered);
    }

    [Theory]
    [InlineData(8, 0)]    // size changed
    [InlineData(7, 1)]    // mtime changed by one tick
    public async Task HashCache_Should_BeIgnored_WhenSizeOrMtimeDiffer(long cachedSize, long tickOffset)
    {
        _temp.WriteFile("big.bin", "content", LongAgo);

        ScannedFolder scan = await ScanAsync(cache: Cache(("big.bin", cachedSize, LongAgo.AddTicks(tickOffset), ManifestBuilder.HashOf("stale"))));

        scan.Entries[P("big.bin")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("content"));
        scan.HashCache[P("big.bin")].Sha256.ShouldBe(ManifestBuilder.HashOf("content"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1.9, false)]
    [InlineData(1.9, false)]   // mtime in the future (clock skew) is racy too
    [InlineData(-2, true)]
    [InlineData(-60, true)]
    public async Task RacyMtime_Should_KeepAFileModifiedWithinTwoSecondsOfHashing_OutOfTheCache(double secondsFromNow, bool cached)
    {
        _temp.WriteFile("fresh.txt", "x", Now.AddSeconds(secondsFromNow));

        ScannedFolder scan = await ScanAsync();

        scan.Entries.ContainsKey(P("fresh.txt")).ShouldBeTrue();
        scan.HashCache.ContainsKey(P("fresh.txt")).ShouldBe(cached);
    }

    [Fact]
    public async Task AFileThatChangesWhileBeingHashed_Should_BeSkippedAsUnstable()
    {
        _temp.WriteFile("growing.log", "line 1\n", LongAgo).WriteFile("calm.txt", "calm", LongAgo);
        CountingHasher hasher = new() { DuringHash = name => { if (name == "growing.log") { File.AppendAllText(_temp.Combine("growing.log"), "line 2\n"); } } };

        ScannedFolder scan = await ScanAsync(hasher: hasher);

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["calm.txt"]);
        scan.Skipped.ShouldBe([new SkippedLocalPath("growing.log", LocalSkipReason.Unstable, "it changed while it was being hashed")]);
        scan.HashCache.ContainsKey(P("growing.log")).ShouldBeFalse();
    }

    [Fact]
    public async Task ALockedFile_Should_BeSkippedAsUnreadable_AndTheRestScanned()
    {
        _temp.WriteFile("locked.db", "data", LongAgo).WriteFile("ok.txt", "ok", LongAgo);
        await using FileStream holder = new(_temp.Combine("locked.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["ok.txt"]);
        scan.Skipped.Single().Path.ShouldBe("locked.db");
        scan.Skipped.Single().Reason.ShouldBe(LocalSkipReason.Unreadable);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AnUnreadableDirectory_Should_BeSkippedAsUnreadable_WithNothingUnderIt()
    {
        SkipUnlessPermissionsApply();
        _temp.WriteFile("private/secret.txt", "s", LongAgo).WriteFile("public.txt", "p", LongAgo);
        File.SetUnixFileMode(_temp.Combine("private"), UnixFileMode.None);

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["public.txt"]);
        scan.Skipped.Single().ShouldBe(scan.Skipped.Single() with { Path = "private", Reason = LocalSkipReason.Unreadable });
    }

    [Fact]
    public async Task Symlinks_Should_BeSkipped_NotFollowed()
    {
        _temp.WriteFile("real/file.txt", "r", LongAgo);
        try
        {
            Directory.CreateSymbolicLink(_temp.Combine("dirlink"), _temp.Combine("real"));
            File.CreateSymbolicLink(_temp.Combine("filelink.txt"), _temp.Combine("real/file.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["real/file.txt"]);
        scan.Skipped.Select(skip => (skip.Path, skip.Reason)).ShouldBe([("dirlink", LocalSkipReason.Symlink), ("filelink.txt", LocalSkipReason.Symlink)]);
    }

    [Fact]
    public async Task ExecutableBit_Should_BeReadFromTheFileMode_OnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bits on Windows");
        _temp.WriteFile("run.sh", "#!/bin/sh", LongAgo, executable: true).WriteFile("data.txt", "d", LongAgo);

        ScannedFolder scan = await ScanAsync();

        scan.Entries[P("run.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();
        scan.Entries[P("data.txt")].ShouldBeOfType<FileEntry>().Executable.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecutableBit_Should_ComeFromTheBase_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows can't see the bit");
        _temp.WriteFile("run.sh", "#!/bin/sh", LongAgo).WriteFile("new.sh", "#!/bin/sh", LongAgo);
        Dictionary<SharePath, ManifestEntry> baseEntries = new() { [P("run.sh")] = FileEntry.Create(ManifestBuilder.HashOf("old"), 3, new DateTimeOffset(LongAgo), executable: true).Value };

        ScannedFolder scan = await ScanAsync(baseEntries: baseEntries);

        scan.Entries[P("run.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeTrue();
        scan.Entries[P("new.sh")].ShouldBeOfType<FileEntry>().Executable.ShouldBeFalse();
    }

    [Fact]
    public async Task DecomposedNames_Should_BeReportedInNfc()
    {
        _temp.WriteFile("résumé/café.txt", "x", LongAgo);

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Single().Value.ShouldBe("résumé/café.txt");
    }

    [Fact]
    public async Task TwoNamesThatAreTheSameInNfc_Should_KeepOne_AndSkipTheOther()
    {
        // APFS won't hold both spellings; Linux and NTFS will.
        _temp.WriteFile("café.txt", "composed", LongAgo);
        _temp.WriteFile("café.txt", "decomposed", LongAgo);
        Assert.SkipWhen(Directory.GetFiles(_temp.Path).Length < 2, "this filesystem treats both spellings as one name");

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Single().Value.ShouldBe("café.txt");
        scan.Skipped.Single().Reason.ShouldBe(LocalSkipReason.InvalidName);
    }

    [Fact]
    public async Task ANameWithABackslash_Should_BeSkippedAsInvalid()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a backslash is a separator on Windows");
        _temp.WriteFile("ok.txt", "ok", LongAgo);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "a\\b.txt"), "x", Ct);

        ScannedFolder scan = await ScanAsync();

        scan.Entries.Keys.Select(path => path.Value).ShouldBe(["ok.txt"]);
        scan.Skipped.ShouldBe([scan.Skipped.Single() with { Path = "a\\b.txt", Reason = LocalSkipReason.InvalidName }]);
    }

    [Fact]
    public async Task AnEmptyFile_Should_HaveTheHashOfNothing_WithoutBeingOpened()
    {
        _temp.WriteFile("empty.txt", "", LongAgo);
        CountingHasher hasher = new();

        ScannedFolder scan = await ScanAsync(hasher: hasher);

        scan.Entries[P("empty.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf(""));
        hasher.Hashed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFifo_Should_NotHangTheScan()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no FIFOs on Windows");
        using var mkfifo = System.Diagnostics.Process.Start("mkfifo", _temp.Combine("pipe"));
        await mkfifo.WaitForExitAsync(Ct);
        _temp.WriteFile("ok.txt", "ok", LongAgo);

        ScannedFolder scan = await ScanAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        scan.Entries.ContainsKey(P("ok.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Summarize_Should_CountFilesAndBytes_LeavingOutIgnoredFilesAndSymlinks()
    {
        _temp.WriteFile("a.txt", "12345").WriteFile("docs/b.md", "123").WriteFile("debug.log", "xxxxxxxxxx").WriteFile(".DS_Store", "finder");
        CountingHasher hasher = new();

        FolderSummary summary = (await Scanner(hasher).SummarizeAsync(new GitIgnoreMatcher(["*.log"]), Ct)).Value;

        summary.ShouldBe(new FolderSummary(2, 8));
        hasher.Hashed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Scan_Should_GiveTheSameResult_WithManyFilesHashedInParallel()
    {
        foreach (int i in Enumerable.Range(0, 200))
        {
            _temp.WriteFile($"d{i % 7}/f{i:D3}.txt", $"content {i}", LongAgo);
        }

        ScannedFolder first = await ScanAsync();
        ScannedFolder second = await ScanAsync();

        first.Entries.Count.ShouldBe(200);
        second.Entries.ShouldBe(first.Entries);
        first.Entries[P("d3/f003.txt")].ShouldBeOfType<FileEntry>().Sha256.ShouldBe(ManifestBuilder.HashOf("content 3"));
    }

    public void Dispose() => _temp.Dispose();

    private static void SkipUnlessPermissionsApply()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        Assert.SkipWhen(Environment.UserName == "root", "root reads everything");
    }

    private FileScanner Scanner(IContentHasher? hasher = null) => new(new ShareRoot(_temp.Path), hasher ?? new Sha256ContentHasher(), _clock);

    private async Task<ScannedFolder> ScanAsync(
        IIgnoreMatcher? ignore = null,
        ImmutableSortedDictionary<SharePath, HashCacheEntry>? cache = null,
        IReadOnlyDictionary<SharePath, ManifestEntry>? baseEntries = null,
        IContentHasher? hasher = null)
    {
        ScanRequest request = new(
            ignore ?? GitIgnoreMatcher.BuiltInsOnly,
            cache ?? Cache(),
            baseEntries ?? new Dictionary<SharePath, ManifestEntry>());

        SharedKernel.Result<ScannedFolder> result = await Scanner(hasher).ScanAsync(request, NullSyncProgressReporter.Instance, Ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        return result.Value;
    }

    private static ImmutableSortedDictionary<SharePath, HashCacheEntry> Cache(params (string Path, long Size, DateTime Mtime, ContentHash Hash)[] entries) =>
        entries.ToImmutableSortedDictionary(entry => P(entry.Path), entry => new HashCacheEntry(entry.Size, entry.Mtime.Ticks, entry.Hash), SharePath.OrdinalComparer);

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);

    private sealed class NothingIgnored : IIgnoreMatcher
    {
        public bool IsIgnored(SharePath path, bool isDirectory) => false;
    }

    /// <summary>The real hasher, recording which files it read and optionally changing one mid-read.</summary>
    private sealed class CountingHasher : IContentHasher
    {
        private readonly Sha256ContentHasher _inner = new();
        private readonly Lock _gate = new();
        private readonly List<string> _hashed = [];

        public Action<string>? DuringHash { get; init; }

        public IReadOnlyList<string> Hashed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _hashed.Order(StringComparer.Ordinal)];
                }
            }
        }

        public async Task<ContentHash> HashAsync(Stream content, CancellationToken cancellationToken)
        {
            if (content is FileStream file)
            {
                string name = Path.GetFileName(file.Name);
                lock (_gate)
                {
                    _hashed.Add(name);
                }

                DuringHash?.Invoke(name);
            }

            return await _inner.HashAsync(content, cancellationToken);
        }

        public IIncrementalContentHash CreateIncremental() => _inner.CreateIncremental();
    }
}
