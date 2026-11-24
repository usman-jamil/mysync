using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Session;
using MySync.Application.Sync.Pull;
using MySync.Domain.Shares;
using MySync.Testing;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.Runtime;
using MySync.Tui.State;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.Machine;

namespace MySync.EndToEndTests;

/// <summary>
/// <c>mysync tui</c>'s runtime driven by a scripted terminal against the real composition
/// (Application + Infrastructure + the provider on real storage, real folders) (CLAUDE.md §10d).
/// </summary>
public sealed class AzureTuiTests(StorageFixture storage) : TuiTests(storage.Azure);

public sealed class S3TuiTests(StorageFixture storage) : TuiTests(storage.S3);

/// <summary>Written once, run once per provider.</summary>
public abstract class TuiTests : IDisposable
{
    private readonly Machine _laptop;
    private readonly Machine _server;
    private readonly Machine _collector;

    protected TuiTests(Backend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        string place = backend.NewPlace();
        _laptop = new Machine(backend, place);
        _server = new Machine(backend, place);
        _collector = new Machine(backend, place);
    }

    [Fact]
    public async Task TheInterface_Should_Upload_Download_Resolve_AndPush_OnRealStorage()
    {
        _laptop.Folder.WriteFile("config/app.yml", "port: 80\n").WriteFile("docs/readme.md", "# readme\n");
        Ok(await _laptop.CreateNamedAsync("tui-e2e"));
        Ok(await _server.CloneAsync("tui-e2e"));
        _server.Folder.WriteFile("docs/runbook.md", "restart it\n");

        ScriptedTerminal terminal = new ScriptedTerminal()
            .WaitFor("scanned")
            .WaitFor("↑ new here")
            .Press('s')
            .WaitFor("Sync finished")
            .WaitFor("Everything is in sync")
            .ThenAsync(async () =>
            {
                _laptop.Folder.WriteFile("config/added-by-laptop.yml", "x: 1\n");
                Ok(await _laptop.SyncAsync()); // another machine's CLI sync
            })
            .Press('r')
            .WaitFor("↓ new in share")
            .Press('s')
            .WaitFor("Sync finished")
            .WaitFor("Everything is in sync") // the rescan that follows a sync has finished: a key now starts a new one
            .ThenAsync(async () =>
            {
                _laptop.Folder.WriteFile("config/app.yml", "port: 8080 (laptop)\n");
                Ok(await _laptop.SyncAsync());
                _server.Folder.WriteFile("config/app.yml", "port: 9090 (server)\n");
            })
            .Press('r')
            .WaitFor("! conflict")
            .Press('c')
            .WaitFor("Found by the rescan")
            .Press('m')
            .WaitFor("Press s to sync first")
            .Press(KeyCode.Escape)
            .Press('s')
            .WaitFor("1 conflict left as it is")
            .WaitFor("scanned")
            .Press('c')
            .WaitFor("1 needs a decision")
            .Press('m')
            .WaitFor("Resolve finished")
            .WaitFor("No conflicts.")
            .Press(KeyCode.Escape)
            .WaitFor("Everything is in sync") // the rescan after the resolve is done, so p isn't turned away
            .Press('/').Type("docs").Press(KeyCode.Enter).Press(KeyCode.Home)
            .Press('p')
            .WaitFor("Push a copy to another share")
            .Type("tui-e2e-logs")
            .Press(KeyCode.Tab).Press(' ')
            .Press(KeyCode.Enter)
            .WaitFor("Pushed 2 files")
            .Press('q');

        await using ServiceProvider provider = _server.BuildProvider();
        TuiRuntime runtime = new(
            terminal,
            provider.GetRequiredService<IFolderSession>(),
            new TuiOptions(false, ColorDepth.TrueColor, _server.Folder.Path, "SERVER", "svc@SERVER"),
            TimeProvider.System);

        int exit = await runtime.RunAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        terminal.Leaves.ShouldBe(1);

        // Up: the runbook reached the share; down: the laptop's file reached the server; the conflict kept the server's version.
        Ok(await _laptop.SyncAsync());
        _laptop.Folder.ReadText("docs/runbook.md").ShouldBe("restart it\n");
        _server.Folder.ReadText("config/added-by-laptop.yml").ShouldBe("x: 1\n");
        _laptop.Folder.ReadText("config/app.yml").ShouldBe("port: 9090 (server)\n");

        // The pushed folder is in the other share, and a third machine can pull it.
        PullReport pulled = Ok(await _collector.PullAsync(new PullCommand(ShareReference.Parse("tui-e2e-logs").Value, [], Latest: true)));
        string folder = pulled.Selected.ShouldHaveSingleItem().LocalPath;
        _collector.Folder.ReadText($"{folder}/docs/runbook.md").ShouldBe("restart it\n");
        _collector.Folder.ReadText($"{folder}/docs/readme.md").ShouldBe("# readme\n");
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _server.Dispose();
        _collector.Dispose();
        GC.SuppressFinalize(this);
    }
}
