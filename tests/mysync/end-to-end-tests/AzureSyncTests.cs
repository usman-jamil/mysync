using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
using static MySync.EndToEndTests.AzureMachine;

namespace MySync.EndToEndTests;

/// <summary>
/// Everything real but the network: two temp folders as two machines, the full DI graph, and
/// Azurite as the storage account (CLAUDE.md §14 "Azurite suites").
/// </summary>
public sealed class AzureSyncTests : IDisposable
{
    private static readonly DateTime OldMtime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly AzuriteFixture _azurite;
    private readonly AzureMachine _a;
    private readonly AzureMachine _b;

    public AzureSyncTests(AzuriteFixture azurite)
    {
        _azurite = azurite;
        string container = AzuriteEnvironment.NewContainerName();
        _a = new AzureMachine(azurite.ConnectionString, container);
        _b = new AzureMachine(azurite.ConnectionString, container);
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
    public async Task TheStateFile_Should_RecordTheAccountAndContainer_AndNoSecret()
    {
        _a.Folder.WriteFile("a.txt", "a");
        await _a.CreateAsync();

        string state = _a.Folder.ReadText(StateFileNames.State);
        state.ShouldContain("\"provider\": \"azure-blob\"");
        state.ShouldContain($"\"accountName\": \"{_azurite.AccountName}\"");
        state.ShouldContain($"\"container\": \"{_a.Container}\"");
        state.ShouldNotContain("AccountKey", Case.Insensitive);
        state.ShouldNotContain(AccountKeyOf(_azurite.ConnectionString));
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
    public async Task AFiveMegabyteFile_Should_RoundTripByteForByte()
    {
        byte[] content = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 7); // two 4 MiB blocks
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
    public async Task AFolderPointedAtAnotherContainer_Should_StopWithStorageMismatch()
    {
        _a.Folder.WriteFile("a.txt", "a");
        await _a.CreateAsync();
        _a.Container = AzuriteEnvironment.NewContainerName();

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
        BlobContainerClient container = new(_azurite.ConnectionString, _a.Container);
        List<string> left = [];
        await foreach (BlobItem blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"shares/{shareId}/", TestContext.Current.CancellationToken))
        {
            left.Add(blob.Name);
        }

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
    }

    private Task<Manifest> HeadAsync(ShareId shareId) =>
        _a.WithStoreAsync(async store => (await store.GetManifestAsync(shareId, TestContext.Current.CancellationToken)).Value!.Manifest);

    private static string AccountKeyOf(string connectionString) =>
        connectionString.Split(';').Single(part => part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase))["AccountKey=".Length..];
}
