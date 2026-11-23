using MySync.Domain.Shares;
using MySync.Testing;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using MySync.Tui.Views;
using Shouldly;
using Xunit;

namespace MySync.Tui.UnitTests;

/// <summary>
/// Whole sessions of <c>mysync tui</c> against the in-memory fakes: scripted keys, the real folder
/// session and handlers, and frames recorded (CLAUDE.md §10d). The terminal is always given back.
/// </summary>
public class RuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A laptop's share <c>app-config</c>, cloned by B (the machine running the interface), with changes on both sides and a conflict.</summary>
    private static async Task<(TuiWorld World, TuiMachine Laptop, TuiMachine B, ShareId ShareId)> Setup()
    {
        TuiWorld world = new();
        TuiMachine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Write("config/app.yml", "port: 80").Write("deploy/deploy.ps1", "deploy v1").Write("deploy/notes.md", "notes");
        ShareId shareId = await laptop.CreateAsync("app-config");
        TuiMachine b = world.Machine("ali@APP-SRV-02");
        await b.CloneAsync("app-config");
        foreach (string path in new[] { "config/app.yml", "deploy/deploy.ps1", "deploy/notes.md" })
        {
            b.Sources.WriteFile($"{b.Sources.CurrentDirectory}/{path}", b.Folder.ReadText(path));
        }

        laptop.Write("config/app.yml", "port: 8080").Write("deploy/deploy.ps1", "deploy v2 (laptop)");
        await laptop.SyncAsync();
        b.Write("deploy/deploy.ps1", "deploy v2 (b)").Write("deploy/new-here.txt", "new");
        return (world, laptop, b, shareId);
    }

    [Fact]
    public async Task AWholeSession_Should_Rescan_Sync_Resolve_Push_AndQuit()
    {
        (TuiWorld world, TuiMachine laptop, TuiMachine b, ShareId shareId) = await Setup();
        await world.Machine("svc@COLLECTOR").InitializeAsync("server-logs");
        world.Store.DownloadStream = (_, bytes) => new SlowStream(bytes, TimeSpan.FromMilliseconds(300));

        ScriptedTerminal terminal = new ScriptedTerminal()
            .WaitFor("scanned")
            .WaitFor("↓ changed in share")
            .WaitFor("↑ new here")
            .WaitFor("! conflict")
            .Press('s')
            .WaitFor("Syncing with the share")
            .WaitFor("Sync finished")
            .WaitFor("scanned")
            .Press('c')
            .WaitFor("1 needs a decision")
            .Press('m')
            .WaitFor("Resolve finished")
            .WaitFor("No conflicts.")
            .Press(KeyCode.Escape)
            .Press('/').Type("deploy").Press(KeyCode.Enter).Press(KeyCode.Home)
            .Press('p')
            .WaitFor("Push a copy to another share")
            .Type("server-logs")
            .Press(KeyCode.Enter)
            .WaitFor("Pushed 3 files")
            .Press('q');

        int exit = await b.Runtime(terminal).RunAsync(Ct);

        exit.ShouldBe(0);
        terminal.Leaves.ShouldBe(1);
        terminal.IsShowing.ShouldBeFalse();
        terminal.Frames.ShouldContain(frame => frame.ToText().Contains("Upload ", StringComparison.Ordinal) || frame.ToText().Contains("Download ", StringComparison.Ordinal));

        // The sync downloaded the laptop's change and uploaded B's new file; resolve kept B's script.
        b.Folder.ReadText("config/app.yml").ShouldBe("port: 8080");
        await laptop.SyncAsync();
        laptop.Folder.ReadText("deploy/new-here.txt").ShouldBe("new");
        laptop.Folder.ReadText("deploy/deploy.ps1").ShouldBe("deploy v2 (b)");

        // The push went to the other share only.
        ShareId logs = (await world.Aliases.GetAsync(ShareName.Create("server-logs").Value, Ct)).Value!.Alias.ShareId;
        Domain.Manifests.Manifest pushed = (await world.Store.GetManifestAsync(logs, Ct)).Value!.Manifest;
        pushed.Entries.Keys.Select(path => path.Value).ShouldContain(path => path.EndsWith("/deploy/new-here.txt", StringComparison.Ordinal));
        (await world.Store.GetManifestAsync(shareId, Ct)).Value!.Manifest.Entries.Keys.ShouldAllBe(path => !path.Value.StartsWith("APP-SRV-02/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CtrlC_DuringAJob_Should_CancelIt_AndASecondEndTheInterface_RestoringTheTerminal()
    {
        (TuiWorld world, _, TuiMachine b, _) = await Setup();
        // Slow, and deaf to the first cancel for a moment, so the job is still running at the second Ctrl+C.
        world.Store.DownloadStream = (_, bytes) => new SlowStream(bytes, TimeSpan.FromMilliseconds(1500), honourCancel: false);
        ScriptedTerminal terminal = new ScriptedTerminal()
            .WaitFor("scanned")
            .Press('s')
            .WaitFor("Syncing with the share")
            .Press(KeyCode.CtrlC)
            .WaitFor("Cancelling")
            .Press(KeyCode.CtrlC);

        int exit = await b.Runtime(terminal).RunAsync(Ct);

        exit.ShouldBe(130);
        terminal.Leaves.ShouldBe(1);
        b.Session.Running.ShouldBeNull();
    }

    [Fact]
    public async Task AnExceptionFromAView_Should_StillRestoreTheTerminal()
    {
        (_, _, TuiMachine b, _) = await Setup();
        int draws = 0;
        ScriptedTerminal terminal = new ScriptedTerminal().WaitFor("scanned").Press('j');
        Frame Failing(TuiState state) => ++draws > 2 ? throw new InvalidOperationException("a broken view") : Screens.Render(state);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => b.Runtime(terminal, Failing).RunAsync(Ct));

        thrown.Message.ShouldBe("a broken view");
        terminal.Enters.ShouldBe(1);
        terminal.Leaves.ShouldBe(1);
    }

    [Fact]
    public async Task AStopRequest_Should_EndTheInterface_AndRestoreTheTerminal()
    {
        (_, _, TuiMachine b, _) = await Setup();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ScriptedTerminal terminal = new ScriptedTerminal().WaitFor("scanned").Then(stop.Cancel).WaitFor("never shown");

        int exit = await b.Runtime(terminal).RunAsync(stop.Token);

        exit.ShouldBe(0);
        terminal.Leaves.ShouldBe(1);
    }

    [Fact]
    public async Task AResize_Should_Relayout_AndATooSmallWindowSaysSo()
    {
        (_, _, TuiMachine b, _) = await Setup();
        var terminal = new ScriptedTerminal();
        terminal
            .WaitFor("scanned")
            .Then(() => terminal.Size = (60, 20))
            .WaitFor("Make the window at least 80 × 24")
            .Then(() => terminal.Size = (120, 40))
            .WaitUntil(screen => screen.Split('\n')[0].Length > 100, "a wider header")
            .Press('q');

        (await b.Runtime(terminal).RunAsync(Ct)).ShouldBe(0);

        terminal.Frames[^1].Width.ShouldBe(120);
    }

    [Fact]
    public async Task ANewEmptyShare_Should_ShowItsFilesAsNotYetUploaded_AndTheFirstSyncUploadsThem()
    {
        TuiWorld world = new();
        TuiMachine laptop = world.Machine("usman@DEV-LAPTOP");
        laptop.Write("a.txt", "a").Write("docs/b.md", "b");
        (await laptop.InitializeAsync("fresh-share")).IsSuccess.ShouldBeTrue();
        ScriptedTerminal terminal = new ScriptedTerminal()
            .WaitFor("2 files here aren't in the share yet. Press s to upload them.")
            .Press('s')
            .WaitFor("Sync finished")
            .WaitFor("Everything is in sync")
            .Press('q');

        (await laptop.Runtime(terminal).RunAsync(Ct)).ShouldBe(0);

        world.Store.ObjectUploads.ShouldBe(2);
    }

    /// <summary>A stored object that takes a while to arrive: the progress view gets drawn.</summary>
    private sealed class SlowStream(byte[] bytes, TimeSpan delay, bool honourCancel = true) : MemoryStream(bytes, writable: false)
    {
        private bool _waited;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                _waited = true;
                await Task.Delay(delay, honourCancel ? cancellationToken : CancellationToken.None);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
