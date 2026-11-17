using System.Text;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.State;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.State;

/// <summary><c>.mysynctool</c> (CLAUDE.md §7): exact shape, atomic writes, and never "new share" on damage.</summary>
public sealed class JsonShareStateStoreTests : IDisposable
{
    private static readonly ShareId Id = ShareId.TryParse("0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", out ShareId? id) ? id : null!;
    private static readonly StorageLocation Location = new("azure-blob", "teamsyncstorage", "mysync");
    private static readonly DateTimeOffset Seen = new(2026, 11, 16, 9, 2, 55, TimeSpan.Zero);

    private readonly TempShareFolder _temp = new();
    private readonly JsonShareStateStore _store;

    public JsonShareStateStoreTests() => _store = new JsonShareStateStore(new ShareRoot(_temp.Path));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_Should_WriteExactlyThisJson()
    {
        (await _store.SaveAsync(Minimal(), Ct)).IsSuccess.ShouldBeTrue();

        _temp.ReadText(StateFileNames.State).ShouldBe($$"""
            {
              "formatVersion": 1,
              "provider": "azure-blob",
              "accountName": "teamsyncstorage",
              "container": "mysync",
              "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10",
              "baseRevision": 17,
              "base": {
                "docs/readme.md": {
                  "type": "file",
                  "sha256": "{{Hash("base")}}",
                  "size": 4,
                  "mtimeUtc": "2026-11-15T18:31:02Z",
                  "exec": false
                }
              },
              "hashCache": {
                "docs/readme.md": {
                  "size": 4,
                  "mtimeTicksUtc": 639000000000000000,
                  "sha256": "{{Hash("base")}}"
                }
              },
              "conflicts": [
                {
                  "path": "reports/q3.xlsx",
                  "kind": "modify/modify",
                  "base": {
                    "type": "file",
                    "sha256": "{{Hash("base")}}",
                    "size": 4,
                    "mtimeUtc": "2026-11-15T18:31:02Z",
                    "exec": false
                  },
                  "local": {
                    "type": "file",
                    "sha256": "{{Hash("local")}}",
                    "size": 5,
                    "mtimeUtc": "2026-11-16T08:55:10Z",
                    "exec": false
                  },
                  "remote": null,
                  "remoteRevision": 18,
                  "remoteUpdatedBy": "ali@SERVER01",
                  "firstDetectedUtc": "2026-11-16T09:02:55Z",
                  "lastSeenUtc": "2026-11-16T09:02:55Z"
                }
              ]
            }
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task RoundTrip_Should_KeepEverything()
    {
        ShareState original = Full();
        (await _store.SaveAsync(original, Ct)).IsSuccess.ShouldBeTrue();

        ShareState read = (await _store.LoadAsync(Ct)).Value!;

        read.Location.ShouldBe(original.Location);
        read.ShareId.ShouldBe(original.ShareId);
        read.BaseRevision.ShouldBe(original.BaseRevision);
        read.Base.ShouldBe(original.Base);
        read.HashCache.ShouldBe(original.HashCache);
        read.Conflicts.ShouldBe(original.Conflicts);
    }

    [Fact]
    public async Task Load_Should_ReturnNull_WhenThereIsNoStateFile()
    {
        Result<ShareState?> loaded = await _store.LoadAsync(Ct);

        loaded.IsSuccess.ShouldBeTrue();
        loaded.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "formatVersion": 1 }""")]
    [InlineData("""{ "formatVersion": 0, "provider": "p", "accountName": "a", "container": "c", "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", "baseRevision": 1, "base": {}, "hashCache": {}, "conflicts": [] }""")]
    [InlineData("""{ "formatVersion": 1, "provider": "p", "accountName": "a", "container": "c", "shareId": "nope", "baseRevision": 1, "base": {}, "hashCache": {}, "conflicts": [] }""")]
    [InlineData("""{ "formatVersion": 1, "provider": "p", "accountName": "a", "container": "c", "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", "baseRevision": 1, "base": { "../x": { "type": "dir" } }, "hashCache": {}, "conflicts": [] }""")]
    [InlineData("""{ "formatVersion": 1, "provider": "", "accountName": "a", "container": "c", "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", "baseRevision": 1, "base": {}, "hashCache": {}, "conflicts": [] }""")]
    public async Task ADamagedStateFile_Should_BeCorrupt_NeverTreatedAsNoShare(string content)
    {
        await File.WriteAllTextAsync(_temp.Combine(StateFileNames.State), content, Ct);

        (await _store.LoadAsync(Ct)).Error.Code.ShouldBe("State.Corrupt");
    }

    [Fact]
    public async Task ANewerFormat_Should_BeUnsupported()
    {
        await File.WriteAllTextAsync(_temp.Combine(StateFileNames.State), """{ "formatVersion": 7, "whatever": "changed" }""", Ct);

        (await _store.LoadAsync(Ct)).Error.ShouldBe(StateErrors.UnsupportedFormat(7));
    }

    [Fact]
    public async Task AnUnusableHashCacheEntry_Should_BeDropped_NotFailTheLoad()
    {
        (await _store.SaveAsync(Minimal(), Ct)).IsSuccess.ShouldBeTrue();
        string json = _temp.ReadText(StateFileNames.State).Replace("\"mtimeTicksUtc\"", "\"ignored\": 1, \"mtimeTicksUtc\"", StringComparison.Ordinal);
        json = json.Replace("\"hashCache\": {", "\"hashCache\": { \"../bad\": { \"size\": 1, \"mtimeTicksUtc\": 1, \"sha256\": \"zz\" },", StringComparison.Ordinal);
        await File.WriteAllTextAsync(_temp.Combine(StateFileNames.State), json, Ct);

        ShareState read = (await _store.LoadAsync(Ct)).Value!;

        read.HashCache.Keys.Select(path => path.Value).ShouldBe(["docs/readme.md"]);
    }

    [Fact]
    public async Task Save_Should_ReplaceTheFileWhole_AndLeaveNoTempFileBehind()
    {
        await File.WriteAllTextAsync(_temp.Combine(StateFileNames.StateTemp), "left over from a crash", Ct);
        (await _store.SaveAsync(Minimal(), Ct)).IsSuccess.ShouldBeTrue();
        (await _store.SaveAsync(Full(), Ct)).IsSuccess.ShouldBeTrue();

        _temp.Exists(StateFileNames.StateTemp).ShouldBeFalse();
        (await _store.LoadAsync(Ct)).Value!.Conflicts.Length.ShouldBe(2);
    }

    [Fact]
    public async Task Save_Should_NeverWriteASecret()
    {
        (await _store.SaveAsync(Full(), Ct)).IsSuccess.ShouldBeTrue();

        string json = _temp.ReadText(StateFileNames.State);
        json.ShouldNotContain("AccountKey", Case.Insensitive);
        json.ShouldNotContain("SharedAccessSignature", Case.Insensitive);
        json.ShouldNotContain("connection", Case.Insensitive);
    }

    public void Dispose() => _temp.Dispose();

    private static ShareState Minimal() => ShareState.Create(
        Location,
        Id,
        Rev(17),
        [new(P("docs/readme.md"), BaseFile)],
        [new(P("docs/readme.md"), new HashCacheEntry(4, 639_000_000_000_000_000, Hash("base")))],
        [new Conflict(P("reports/q3.xlsx"), ConflictKind.ModifyModify, BaseFile, LocalFile, null, Rev(18), "ali@SERVER01", Seen, Seen)]);

    // Every shape: dir entries, exec, a deleted side on each end, and the other conflict kinds.
    private static ShareState Full() => ShareState.Create(
        Location,
        Id,
        Rev(17),
        [new(P("docs/readme.md"), BaseFile), new(P("logs"), DirectoryEntry.Instance), new(P("run.sh"), FileEntry.Create(Hash("sh"), 2, Seen, executable: true).Value)],
        [new(P("docs/readme.md"), new HashCacheEntry(4, 639_000_000_000_000_001, Hash("base")))],
        [
            new Conflict(P("a/type-clash"), ConflictKind.Type, null, DirectoryEntry.Instance, LocalFile, Rev(18), "ali@SERVER01", Seen, Seen.AddHours(1)),
            new Conflict(P("reports/q3.xlsx"), ConflictKind.DeleteModify, BaseFile, null, LocalFile, Rev(18), "ali@SERVER01", Seen, Seen.AddTicks(5)),
        ]);

    private static readonly FileEntry BaseFile = FileEntry.Create(Hash("base"), 4, new DateTimeOffset(2026, 11, 15, 18, 31, 2, TimeSpan.Zero), executable: false).Value;
    private static readonly FileEntry LocalFile = FileEntry.Create(Hash("local"), 5, new DateTimeOffset(2026, 11, 16, 8, 55, 10, TimeSpan.Zero), executable: false).Value;

    private static ContentHash Hash(string content) => ManifestBuilder.HashOf(content);

    private static Revision Rev(long value) => Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentOutOfRangeException(nameof(value));

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);
}
