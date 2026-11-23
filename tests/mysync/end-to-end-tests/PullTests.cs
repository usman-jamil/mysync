using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Pull;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.AzureMachine;

namespace MySync.EndToEndTests;

/// <summary>
/// <c>mysync pull</c> on Azurite with the full DI graph and real folders (CLAUDE.md §10c): a
/// collector fetches what servers pushed, and anyone can download an ordinary share without
/// changing it.
/// </summary>
public sealed class PullTests : IDisposable
{
    private readonly AzureMachine _laptop;
    private readonly AzureMachine _web01;
    private readonly AzureMachine _web02;
    private readonly AzureMachine _collector;
    private readonly string _container = AzuriteEnvironment.NewContainerName();

    public PullTests(AzuriteFixture azurite)
    {
        _laptop = new AzureMachine(azurite.ConnectionString, _container);
        _web01 = new AzureMachine(azurite.ConnectionString, _container);
        _web02 = new AzureMachine(azurite.ConnectionString, _container);
        _collector = new AzureMachine(azurite.ConnectionString, _container);
    }

    private static ShareReference Ref(string share) => ShareReference.Parse(share).Value;

    [Fact]
    public async Task ACollector_Should_PullTheLatestPushOfEachHost_ThenOneFolderWithTo()
    {
        _laptop.Folder.WriteFile("notes.md", "# collection\n");
        Ok(await _laptop.CreateNamedAsync("collect-logs"));
        _web01.Folder.WriteFile("app/error.log", "web01 old\n");
        _web02.Folder.WriteFile("app/error.log", "web02\n").WriteFile("dump.bin", RandomBytes(5 * 1024 * 1024));
        PushReport web01Old = Ok(await _web01.PushAsync(new PushCommand(Ref("collect-logs"), [], Host: "web01")));
        _web01.Folder.WriteFile("app/error.log", "web01 new\n");
        PushReport web01New = Ok(await _web01.PushAsync(new PushCommand(Ref("collect-logs"), [], Host: "web01")));
        PushReport web02 = Ok(await _web02.PushAsync(new PushCommand(Ref("collect-logs"), [], Host: "web02")));

        PullReport latest = Ok(await _collector.PullAsync(new PullCommand(Ref("collect-logs"), [], Latest: true)));
        PullReport one = Ok(await _collector.PullAsync(new PullCommand(Ref("collect-logs"), [web01Old.Target], To: "older")));

        latest.Selected.Select(root => root.SharePath).ShouldBe([web01New.Target, web02.Target]);
        _collector.Folder.ReadText($"{web01New.Target}/app/error.log").ShouldBe("web01 new\n");
        _collector.Folder.ReadText($"{web02.Target}/app/error.log").ShouldBe("web02\n");
        (await File.ReadAllBytesAsync(_collector.Folder.Combine($"{web02.Target}/dump.bin"), TestContext.Current.CancellationToken))
            .ShouldBe(await File.ReadAllBytesAsync(_web02.Folder.Combine("dump.bin"), TestContext.Current.CancellationToken));
        _collector.Folder.Exists($"{web01Old.Target}/app/error.log").ShouldBeFalse();
        _collector.Folder.Exists("notes.md").ShouldBeFalse();

        string folderName = web01Old.Target.Split('/')[1];
        one.Selected.ShouldHaveSingleItem().LocalPath.ShouldBe(folderName);
        _collector.Folder.ReadText($"older/{folderName}/app/error.log").ShouldBe("web01 old\n");
        _collector.Folder.Exists(StateFileNames.State).ShouldBeFalse();
    }

    [Fact]
    public async Task PullingAnOrdinaryShare_Should_LeaveItExactlyAsItWas()
    {
        _laptop.Folder.WriteFile("docs/readme.md", "# readme\n").WriteFile("scripts/deploy.sh", "#!/bin/sh\n", executable: true);
        ShareId shareId = Ok(await _laptop.CreateNamedAsync("team-docs")).ShareId;
        Ok(await _laptop.SyncAsync());
        ManifestSnapshot before = await _laptop.WithStoreAsync(async store => (await store.GetManifestAsync(shareId, TestContext.Current.CancellationToken)).Value!);

        PullReport pulled = Ok(await _collector.PullAsync(new PullCommand(Ref("team-docs"), [])));

        ManifestSnapshot after = await _laptop.WithStoreAsync(async store => (await store.GetManifestAsync(shareId, TestContext.Current.CancellationToken)).Value!);
        after.ETag.ShouldBe(before.ETag);
        after.Manifest.Revision.ShouldBe(before.Manifest.Revision);
        pulled.Revision.ShouldBe(before.Manifest.Revision);
        _collector.Folder.Files().ShouldBe(_laptop.Folder.Files());
        _collector.Folder.IsExecutable("scripts/deploy.sh").ShouldBe(!OperatingSystem.IsWindows());

        SyncReport synced = Ok(await _laptop.SyncAsync());
        synced.Uploaded.Files.ShouldBe(0);
        synced.Downloaded.Files.ShouldBe(0);
        synced.Committed.ShouldBeFalse();
    }

    [Fact]
    public async Task PullingIntoAFolderThatSyncsWithTheShare_Should_BeRefused()
    {
        _laptop.Folder.WriteFile("docs/readme.md", "# readme\n");
        Ok(await _laptop.CreateNamedAsync("team-refuse"));

        SharedKernel.Error error = (await _laptop.PullAsync(new PullCommand(Ref("team-refuse"), [], To: "copy"))).Error;

        error.Code.ShouldBe("Pull.DestinationIsTheShare");
        _laptop.Folder.Exists("copy").ShouldBeFalse();
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _web01.Dispose();
        _web02.Dispose();
        _collector.Dispose();
    }
}
