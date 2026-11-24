using System.Security.Cryptography;
using MySync.Application.Shares.Delete;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.Machine;

namespace MySync.EndToEndTests;

/// <summary>Two-machine sync on real Azure storage.</summary>
public sealed class AzureSyncTests(StorageFixture storage) : SyncTests(storage.Azure);

/// <summary>Two-machine sync on a real S3 bucket.</summary>
public sealed class S3SyncTests(StorageFixture storage) : SyncTests(storage.S3);

/// <summary>
/// Everything real: two temp folders as two machines, the full DI graph, and the real test storage
/// of one provider (CLAUDE.md §14). Written once, run once per provider.
/// </summary>
public abstract class SyncTests : IDisposable
{
    private static readonly DateTime OldMtime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Backend _backend;
    private readonly Machine _a;
    private readonly Machine _b;

    protected SyncTests(Backend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        string place = backend.NewPlace();
        _a = new Machine(backend, place);
        _b = new Machine(backend, place);
    }

    [Fact]
    public async Task CreateCloneAndTwoWaySync_Should_LeaveBothFoldersIdentical()
    {
        _a.Folder.WriteFile("docs/readme.md", "# hi", OldMtime).WriteFile("run.sh", "#!/bin/sh", OldMtime, executable: true).WriteFile("old.txt", "x", OldMtime);
        ShareId shareId = await _a.CreateAsync();
        Ok(await _b.CloneAsync(shareId));

        _a.Folder.WriteFile("docs/readme.md", "# edited on a");
        File.Delete(_a.Folder.Combine("old.txt"));
        _b.Folder.WriteFile("new-on-b/notes.txt", "from b");
        Ok(await _a.SyncAsync());
        Ok(await _b.SyncAsync());
        Ok(await _a.SyncAsync());

        _a.Folder.Files().ShouldBe(_b.Folder.Files());
        _b.Folder.Files().Keys.ShouldBe(["docs/readme.md", "new-on-b/notes.txt", "run.sh"]);
        _b.Folder.IsExecutable("run.sh").ShouldBe(!OperatingSystem.IsWindows());
        Manifest head = await HeadAsync(shareId);
        head.Revision.Value.ShouldBe(3);
    }

    [Fact]
    public async Task TheStateFile_Should_RecordTheProviderAccountAndContainer_AndNoSecret()
    {
        _a.Folder.WriteFile("a.txt", "a");
        await _a.CreateAsync();

        StorageLocation expected = _backend.Location(_a.Place);
        string state = _a.Folder.ReadText(StateFileNames.State);
        state.ShouldContain($"\"provider\": \"{expected.Provider}\"");
        state.ShouldContain($"\"accountName\": \"{expected.AccountName}\"");
        state.ShouldContain($"\"container\": \"{expected.Container}\"");
        state.ShouldNotContain("AccountKey", Case.Insensitive);
        foreach (string secret in _backend.Secrets)
        {
            state.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task TwoMachinesSyncingAtOnce_Should_BothFinish_LoseNothing_AndKeepRevisionsSequential()
    {
        _a.Folder.WriteFile("seed.txt", "seed");
        ShareId shareId = await _a.CreateAsync();
        Ok(await _b.CloneAsync(shareId));

        for (int round = 0; round < 4; round++)
        {
            _a.Folder.WriteFile($"a-{round}.txt", $"a {round}");
            _b.Folder.WriteFile($"b-{round}.txt", $"b {round}");

            Result<SyncReport>[] results = await Task.WhenAll(Task.Run(_a.SyncAsync), Task.Run(_b.SyncAsync));

            results.ShouldAllBe(result => result.IsSuccess);
        }

        Ok(await _a.SyncAsync());
        Ok(await _b.SyncAsync());
        _a.Folder.Files().ShouldBe(_b.Folder.Files());
        _a.Folder.Files().Count.ShouldBe(9);

        List<long> revisions = await _a.WithStoreAsync(async store =>
        {
            List<long> found = [];
            await foreach (Result<Manifest> manifest in store.ListManifestHistoryAsync(shareId, TestContext.Current.CancellationToken))
            {
                found.Add(manifest.Value.Revision.Value);
            }

            return found;
        });
        revisions.ShouldBe([.. Enumerable.Range(1, revisions.Count).Select(revision => (long)revision)]);
        (await HeadAsync(shareId)).Revision.Value.ShouldBe(revisions[^1]);
    }

    [Fact]
    public async Task AFileOfSeveralPartsOrBlocks_Should_RoundTripByteForByte()
    {
        byte[] content = RandomNumberGenerator.GetBytes(17 * 1024 * 1024 + 7); // five 4 MiB Azure blocks, two 16 MiB S3 parts
        _a.Folder.WriteFile("big.bin", content);
        ShareId shareId = await _a.CreateAsync();

        SyncReport cloned = Ok(await _b.CloneAsync(shareId));

        cloned.Downloaded.Bytes.ShouldBe(content.Length);
        (await File.ReadAllBytesAsync(_b.Folder.Combine("big.bin"), TestContext.Current.CancellationToken)).ShouldBe(content);
    }

    [Fact]
    public async Task AConflict_Should_LeaveTheLocalFileAlone_AndBeLogged()
    {
        _a.Folder.WriteFile("report.txt", "base");
        ShareId shareId = await _a.CreateAsync();
        Ok(await _b.CloneAsync(shareId));
        _a.Folder.WriteFile("report.txt", "a's edit");
        Ok(await _a.SyncAsync());
        _b.Folder.WriteFile("report.txt", "b's edit");

        SyncReport report = Ok(await _b.SyncAsync());

        report.Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);
        _b.Folder.ReadText("report.txt").ShouldBe("b's edit");
        _b.Folder.ReadText(StateFileNames.ConflictLog).ShouldContain("CONFLICT modify/modify report.txt");
    }

    [Fact]
    public async Task AFolderPointedAtAnotherContainerOrBucket_Should_StopWithStorageMismatch()
    {
        _a.Folder.WriteFile("a.txt", "a");
        await _a.CreateAsync();
        foreach ((string name, string? value) in _backend.Elsewhere(_a.Place))
        {
            _a.Overrides[name] = value;
        }

        (await _a.SyncAsync()).Error.Code.ShouldBe("State.StorageMismatch");
    }

    [Fact]
    public async Task DeletingAShare_Should_LeaveOnlyTheMarkerInTheContainer_AndStopTheOtherMachine()
    {
        _a.Folder.WriteFile("secret/report.txt", "sensitive", OldMtime).WriteFile("notes.txt", "n", OldMtime);
        ShareId shareId = await _a.CreateAsync();
        Ok(await _b.CloneAsync(shareId));
        _a.Folder.WriteFile("notes.txt", "edited");
        Ok(await _a.SyncAsync());

        DeleteShareResponse deleted = Ok(await _a.DeleteAsync());

        deleted.ObjectsDeleted.ShouldBe(3);
        deleted.RecoverableCopies.ShouldBe(0);
        List<string> left = await _backend.ListAsync(_a.Place, $"shares/{shareId}/");

        left.ShouldBe([$"shares/{shareId}/manifest.json"]);
        _a.Folder.Exists(StateFileNames.State).ShouldBeFalse();
        _a.Folder.Exists(StateFileNames.Lock).ShouldBeFalse();
        _a.Folder.ReadText("secret/report.txt").ShouldBe("sensitive");

        Result<SyncReport> other = await _b.SyncAsync();
        other.Error.Code.ShouldBe("Shares.Deleted");
        _b.Folder.ReadText("secret/report.txt").ShouldBe("sensitive");
    }

    [Fact]
    public async Task CloningAShareThatDoesNotExist_Should_FailWithNotFound()
    {
        var missing = ShareId.New();

        (await _b.CloneAsync(missing)).Error.ShouldBe(ShareErrors.NotFound(missing));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Manifest> HeadAsync(ShareId shareId) =>
        _a.WithStoreAsync(async store => (await store.GetManifestAsync(shareId, TestContext.Current.CancellationToken)).Value!.Manifest);
}
