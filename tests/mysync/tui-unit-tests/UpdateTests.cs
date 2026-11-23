using System.Collections.Immutable;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Session;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.Storage;
using MySync.Domain.Sync;
using MySync.Tui.Effects;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using MySync.Tui.Update;
using Shouldly;
using Xunit;
using static MySync.Tui.UnitTests.Fixtures;

namespace MySync.Tui.UnitTests;

/// <summary><c>Update(state, msg)</c>: every key on every screen, the dialogs, and the session events (CLAUDE.md §10d).</summary>
public class UpdateTests
{
    private static int IndexOf(TuiState state, string path) => state.Rows.IndexOf(state.Rows.First(row => row.Path == path));

    private static string[] Paths(TuiState state) => [.. state.Rows.Select(row => row.IsFolder ? row.Path + "/" : row.Path)];

    [Fact]
    public void TheFirstRescan_Should_FillTheList_FoldersFirstThenNames()
    {
        TuiState state = MainScreen();

        state.Status.ShouldNotBeNull();
        state.ScannedAt.ShouldBe(Now.AddMinutes(-2));
        state.Running.ShouldBeNull();
        Paths(state).ShouldBe([
            "cache/", "cache/build.tmp",
            "config/", "config/app.staging.yml", "config/app.yml", "config/secrets.template.env",
            "data/", "data/locked.db",
            "deploy/", "deploy/deploy.ps1", "deploy/notes.md",
            "logs/", "logs/install-2026-11-22.log",
            "scripts/", "scripts/run.sh",
            "LICENSE", "README.md", "old-readme.txt", "retired.cfg"]);
        state.Rows.Single(row => row.Path == "deploy").Changes.ShouldBe(1);
        state.Rows.Single(row => row.Path == "deploy").Conflicts.ShouldBe(1);
        state.Activity[0].Text.ShouldBe("Rescan: 3 changes here, 3 in the share, 1 conflict.");
        state.Summary.PendingChanges.ShouldBe(6);
    }

    [Fact]
    public void ArrowsAndVimKeys_Should_MoveTheHighlight_WithinTheList()
    {
        TuiState state = MainScreen();

        state = Press(state, Key(KeyCode.Down), KeyInput.Of('j')).State;
        state.Selected.ShouldBe(2);
        state = Press(state, Key(KeyCode.Up), KeyInput.Of('k'), KeyInput.Of('k')).State;
        state.Selected.ShouldBe(0);
        state = Press(state, Key(KeyCode.End)).State;
        state.Selected.ShouldBe(state.Rows.Length - 1);
        state = Press(state, Key(KeyCode.Down)).State;
        state.Selected.ShouldBe(state.Rows.Length - 1);
        state = Press(state, Key(KeyCode.Home)).State;
        state.Selected.ShouldBe(0);
    }

    [Fact]
    public void Paging_Should_KeepTheHighlightVisible()
    {
        TuiState state = MainScreen(80, 24); // 13 rows in view, 19 rows in the list

        state = Press(state, Key(KeyCode.PageDown)).State;
        state.Selected.ShouldBe(13);
        state.Scroll.ShouldBe(1);
        state = Press(state, Key(KeyCode.PageDown)).State;
        state.Selected.ShouldBe(18);
        state.Scroll.ShouldBe(6);
        state = Press(state, Key(KeyCode.PageUp)).State;
        state.Selected.ShouldBe(5);
        state.Scroll.ShouldBe(5);
        (state.Selected >= state.Scroll && state.Selected < state.Scroll + state.ListHeight).ShouldBeTrue();
    }

    [Fact]
    public void LeftAndRight_Should_CloseAndOpenFolders_AndLeftOnAFileJumpsToItsFolder()
    {
        TuiState state = MainScreen();
        state = state with { Selected = IndexOf(state, "config/app.yml") };

        state = Press(state, Key(KeyCode.Left)).State;
        state.SelectedRow!.Path.ShouldBe("config");
        state = Press(state, Key(KeyCode.Left)).State;
        Paths(state).ShouldNotContain("config/app.yml");
        state.SelectedRow!.Expanded.ShouldBeFalse();
        state = Press(state, Key(KeyCode.Right)).State;
        Paths(state).ShouldContain("config/app.yml");
        state = Press(state, Key(KeyCode.Left), Key(KeyCode.Enter)).State;
        Paths(state).ShouldContain("config/app.yml");
    }

    [Fact]
    public void Filters_Should_ShowOnlyMatchingFiles_UnderTheirFolders()
    {
        TuiState state = MainScreen();

        state = Press(state, KeyInput.Of('f')).State;
        state.Filter.ShouldBe(ListFilter.Here);
        Paths(state).ShouldBe(["config/", "config/app.yml", "deploy/", "deploy/notes.md", "old-readme.txt"]);
        state = Press(state, KeyInput.Of('3')).State;
        state.Filter.ShouldBe(ListFilter.Share);
        Paths(state).ShouldBe(["config/", "config/app.staging.yml", "logs/", "logs/install-2026-11-22.log", "retired.cfg"]);
        state = Press(state, KeyInput.Of('F'), KeyInput.Of('F')).State;
        state.Filter.ShouldBe(ListFilter.All);
        state = Press(state, KeyInput.Of('F')).State;
        state.Filter.ShouldBe(ListFilter.Synced);
        state.Summary.FilterCounts.ShouldBe(new Dictionary<ListFilter, int>
        {
            [ListFilter.All] = 13, [ListFilter.Here] = 3, [ListFilter.Share] = 3, [ListFilter.Conflicts] = 1, [ListFilter.Other] = 2, [ListFilter.Synced] = 4,
        });
    }

    [Fact]
    public void Search_Should_NarrowTheList_EnterKeepsIt_EscClearsIt()
    {
        TuiState state = Press(MainScreen(), KeyInput.Of('/')).State;
        state.Searching.ShouldBeTrue();

        state = Type(state, "appx").State;
        state = Press(state, Key(KeyCode.Backspace)).State;
        state.Search.ShouldBe("app");
        Paths(state).ShouldBe(["config/", "config/app.staging.yml", "config/app.yml"]);
        state = Press(state, Key(KeyCode.Enter)).State;
        state.Searching.ShouldBeFalse();
        state.Search.ShouldBe("app");
        state = Press(state, KeyInput.Of('r')).State; // keys work again
        state.Running.ShouldBe(SessionJob.Rescan);
        state = Press(Press(MainScreen(), KeyInput.Of('/')).State, KeyInput.Of('x'), Key(KeyCode.Escape)).State;
        state.Search.ShouldBeEmpty();
        state.Rows.Length.ShouldBe(19);
    }

    [Fact]
    public void Rescan_Should_StartOnce_AndAJobKeyWhileBusy_SaysToWait()
    {
        (TuiState state, ImmutableArray<Effect> effects) = Press(MainScreen(), KeyInput.Of('r'));
        effects.ShouldBe([new StartRescan()]);

        (TuiState busy, ImmutableArray<Effect> none) = Press(state, KeyInput.Of('s'));

        none.ShouldBeEmpty();
        busy.Banner!.Text.ShouldBe("Wait for the rescan to finish, or press Esc to cancel it.");
    }

    [Fact]
    public void Sync_Should_Start_UnlessTheLastRescanFailed()
    {
        Press(MainScreen(), KeyInput.Of('s')).Effects.ShouldBe([new StartSync(false)]);

        TuiState failed = Apply(MainScreen(), new SessionMsg(new JobFailed(SessionJob.Rescan, StorageErrors.Unreachable)));
        (TuiState state, ImmutableArray<Effect> effects) = Press(failed, KeyInput.Of('s'));

        effects.ShouldBeEmpty();
        state.Banner!.Text.ShouldBe(StorageErrors.Unreachable.Description);
        Press(failed, KeyInput.Of('p')).Effects.ShouldBeEmpty();
        Press(failed, KeyInput.Of('r')).Effects.ShouldBe([new StartRescan()]);
    }

    [Fact]
    public void ALargeFirstUpload_Should_AskFirst_WithNoAsTheDefault()
    {
        FolderStatus large = StatusOf([.. Enumerable.Range(0, 1001).Select(i => Entry($"f{i}.txt", FileStatus.NewHere, 10))], 0);
        (TuiState asking, ImmutableArray<Effect> none) = Press(MainScreen(status: large), KeyInput.Of('s'));

        none.ShouldBeEmpty();
        asking.Dialog.ShouldBeOfType<ConfirmDialog>().Kind.ShouldBe(ConfirmKind.LargeUpload);
        Press(asking, Key(KeyCode.Enter)).Effects.ShouldBeEmpty();
        Press(asking, Key(KeyCode.Enter)).State.Dialog.ShouldNotBeNull();
        Press(asking, KeyInput.Of('n')).State.Dialog.ShouldBeNull();
        Press(asking, Key(KeyCode.Escape)).State.Dialog.ShouldBeNull();
        Press(asking, KeyInput.Of('y')).Effects.ShouldBe([new StartSync(false)]);
    }

    [Fact]
    public void AMassDelete_Should_AskBeforeSyncingWithTheFlag()
    {
        TuiState state = Apply(Press(MainScreen(), KeyInput.Of('s')).State, new SessionMsg(new JobStarted(SessionJob.Sync)));

        state = Apply(state, new SessionMsg(new JobFailed(SessionJob.Sync, SyncErrors.MassDeleteBlocked(84, 128))));

        ConfirmDialog dialog = state.Dialog.ShouldBeOfType<ConfirmDialog>();
        dialog.Kind.ShouldBe(ConfirmKind.MassDelete);
        dialog.YesLabel.ShouldBe("Delete 1 file and sync");
        state.Running.ShouldBeNull();
        Press(state, Key(KeyCode.Enter)).Effects.ShouldBeEmpty();
        Press(state, KeyInput.Of('y')).Effects.ShouldBe([new StartSync(true)]);
    }

    [Fact]
    public void Push_OnAnItemOnlyInTheShare_Should_SaySo()
    {
        TuiState state = MainScreen();
        state = state with { Selected = IndexOf(state, "logs/install-2026-11-22.log") };

        (TuiState pushed, ImmutableArray<Effect> effects) = Press(state, KeyInput.Of('p'));

        effects.ShouldBeEmpty();
        pushed.Dialog.ShouldBeNull();
        pushed.Banner!.Text.ShouldBe("Nothing on disk to push for this item.");
    }

    [Fact]
    public void ThePushDialog_Should_EditTheName_MoveBetweenFields_AndOnlyPushAValidName()
    {
        TuiState state = MainScreen();
        state = Press(state with { Selected = IndexOf(state, "deploy") }, KeyInput.Of('p')).State;
        PushDialog dialog = state.Dialog.ShouldBeOfType<PushDialog>();
        (dialog.ItemPath, dialog.ItemIsFolder, dialog.Files, dialog.Bytes, dialog.Create, dialog.Focus).ShouldBe(("deploy", true, 2, 14_745L, false, PushField.Share));

        Press(state, Key(KeyCode.Enter)).Effects.ShouldBeEmpty(); // empty name
        state = Type(state, "Bad!").State;
        state.Dialog.ShouldBeOfType<PushDialog>().Error.ShouldNotBeNull();
        Press(state, Key(KeyCode.Enter)).Effects.ShouldBeEmpty();
        state = Press(state, Key(KeyCode.Backspace), Key(KeyCode.Backspace), Key(KeyCode.Backspace), Key(KeyCode.Backspace)).State;
        state = Type(state, "app-config").State;
        state.Dialog.ShouldBeOfType<PushDialog>().Error.ShouldBe("This folder already syncs with app-config. Choose another share, or use Sync.");
        state = Press(state, [.. Enumerable.Repeat(Key(KeyCode.Backspace), 10)]).State;
        state = Type(state, ShareId.ToString()).State;
        state.Dialog.ShouldBeOfType<PushDialog>().Error.ShouldNotBeNull();
        state = Press(state, [.. Enumerable.Repeat(Key(KeyCode.Backspace), 36)]).State;

        state = Type(state, "server logs").State; // a space is typed into the name field…
        state.Dialog.ShouldBeOfType<PushDialog>().Error.ShouldNotBeNull();
        state = Press(state, [.. Enumerable.Repeat(Key(KeyCode.Backspace), 11)]).State;
        state = Type(state, "server-logs").State;
        state = Press(state, Key(KeyCode.Tab), KeyInput.Of(' ')).State; // …and toggles the checkbox
        dialog = state.Dialog.ShouldBeOfType<PushDialog>();
        (dialog.Share, dialog.Create, dialog.Focus, dialog.Error).ShouldBe(("server-logs", true, PushField.Create, null));
        state = Press(state, Key(KeyCode.Tab, shift: true)).State;
        state.Dialog.ShouldBeOfType<PushDialog>().Focus.ShouldBe(PushField.Share);

        (TuiState pushing, ImmutableArray<Effect> effects) = Press(state, Key(KeyCode.Enter));

        effects.ShouldBe([new StartPush("deploy", "server-logs", true)]);
        pushing.Dialog.ShouldBeNull();
        pushing.Running!.Kind.ShouldBe(JobKind.Push);
    }

    [Fact]
    public void Esc_Should_CloseADialog_OrClearASearch()
    {
        TuiState state = MainScreen();
        Press(Press(state, KeyInput.Of('?')).State, Key(KeyCode.Escape)).State.Dialog.ShouldBeNull();
        Press(Press(state with { Selected = IndexOf(state, "deploy") }, KeyInput.Of('p')).State, Key(KeyCode.Escape)).State.Dialog.ShouldBeNull();
        Press(Type(Press(state, KeyInput.Of('/')).State, "x").State, Key(KeyCode.Enter), Key(KeyCode.Escape)).State.Search.ShouldBeEmpty();
    }

    [Fact]
    public void SessionEvents_Should_MoveTheProgressView()
    {
        TuiState state = Syncing();

        JobView job = state.Job.ShouldNotBeNull();
        job.Title.ShouldBe("Syncing with the share");
        job.Phase.ShouldBe(SyncPhase.Downloading);
        job.Upload.ShouldBe(new DirectionProgress(2, 10_034, 1, 6246, 51_200, "deploy/notes.md"));
        job.Download!.Percent.ShouldBe(30);
        job.Items.Single(item => item.Path == "config/app.yml").State.ShouldBe(ItemState.Done);
        job.Items.Single(item => item.Path == "deploy/notes.md").State.ShouldBe(ItemState.Active);
        job.Items.Single(item => item.Path == "deploy/deploy.ps1").State.ShouldBe(ItemState.Conflict);
        job.Items.Single(item => item.Path == "retired.cfg").Action.ShouldBe("delete here");
    }

    [Fact]
    public void DuringAJob_JobKeys_Should_BeIgnored_EscCancels_AndASecondCtrlCQuits()
    {
        TuiState state = Syncing();

        foreach (char key in "rspc")
        {
            (TuiState ignored, ImmutableArray<Effect> effects) = Press(state, KeyInput.Of(key));
            effects.ShouldBeEmpty();
            ignored.Job.ShouldNotBeNull();
        }

        Press(state, KeyInput.Of('s')).State.Banner!.Text.ShouldBe("Wait for the sync to finish, or press Esc to cancel it.");
        (TuiState cancelling, ImmutableArray<Effect> cancel) = Press(state, Key(KeyCode.Escape));
        cancel.ShouldBe([new CancelJob()]);
        cancelling.Job!.Cancelling.ShouldBeTrue();
        Press(state, Key(KeyCode.CtrlC)).Effects.ShouldBe([new CancelJob()]);
        Press(cancelling, Key(KeyCode.CtrlC)).Effects.ShouldBe([new CancelJob(), new Quit(130)]);
    }

    [Fact]
    public void Quit_Should_AskOnlyWhileAJobRuns()
    {
        Press(MainScreen(), KeyInput.Of('q')).Effects.ShouldBe([new Quit(0)]);
        Press(MainScreen(), Key(KeyCode.CtrlC)).Effects.ShouldBe([new Quit(0)]);

        (TuiState asking, ImmutableArray<Effect> none) = Press(Syncing(), KeyInput.Of('q'));
        none.ShouldBeEmpty();
        asking.Dialog.ShouldBeOfType<ConfirmDialog>().Kind.ShouldBe(ConfirmKind.QuitDuringJob);
        Press(asking, KeyInput.Of('n')).State.Dialog.ShouldBeNull();
        Press(asking, KeyInput.Of('y')).Effects.ShouldBe([new CancelJob(), new Quit(0)]);
    }

    [Fact]
    public void TheConflictsScreen_Should_DecideOneOrAll()
    {
        TuiState state = Press(MainScreen(), KeyInput.Of('c')).State;
        state.Screen.ShouldBe(Screen.Conflicts);

        Press(state, KeyInput.Of('m')).Effects.ShouldBe([new StartResolve(["deploy/deploy.ps1"], false, KeepSide.Local)]);
        Press(state, KeyInput.Of('t')).Effects.ShouldBe([new StartResolve(["deploy/deploy.ps1"], false, KeepSide.Remote)]);
        Press(state, KeyInput.Of('M')).Effects.ShouldBe([new StartResolve([], true, KeepSide.Local)]);
        Press(state, KeyInput.Of('T')).Effects.ShouldBe([new StartResolve([], true, KeepSide.Remote)]);
        Press(state, Key(KeyCode.Escape)).State.Screen.ShouldBe(Screen.Main);
        Press(Press(state, KeyInput.Of('m')).State, KeyInput.Of('t')).State.Banner!.Text.ShouldStartWith("Wait for the resolve");
    }

    [Fact]
    public void TheDiff_Should_Load_Scroll_AndDecide()
    {
        (TuiState state, ImmutableArray<Effect> effects) = Press(MainScreen(), KeyInput.Of('c'), KeyInput.Of('d'));
        effects.ShouldBe([new LoadDiff("deploy/deploy.ps1")]);
        state.Diff!.Loading.ShouldBeTrue();

        state = Apply(state, new DiffLoaded("deploy/deploy.ps1", null, StorageErrors.Unreachable));
        state.Diff!.Error.ShouldBe(StorageErrors.Unreachable.Description);
        state = Press(state, Key(KeyCode.Escape)).State;
        state.Screen.ShouldBe(Screen.Conflicts);

        state = Press(state, KeyInput.Of('d')).State;
        Press(state, KeyInput.Of('m')).Effects.ShouldBe([new StartResolve(["deploy/deploy.ps1"], false, KeepSide.Local)]);
        Press(state, KeyInput.Of('t')).State.Screen.ShouldBe(Screen.Conflicts);
        Press(state, Key(KeyCode.Down)).State.Diff!.Scroll.ShouldBe(0); // nothing to scroll yet
    }

    [Fact]
    public void AFinishedSync_Should_SayWhatHappened()
    {
        TuiState state = Apply(Syncing(), new SessionMsg(new JobFinished(SessionJob.Sync, new JobResult(Sync: Report()))));

        state.Job.ShouldBeNull();
        state.Running.ShouldBeNull();
        state.Banner!.Text.ShouldBe("✓ Sync finished  2 uploaded · 2 downloaded · 2 deleted · the share is at revision 43");
        state.Banner.Role.ShouldBe(Role.Ok);
        state.Activity[0].Text.ShouldBe("Sync: 2 uploaded, 2 downloaded, 2 deleted.");
    }

    [Fact]
    public void ACancelledJob_AndARefusal_Should_ShowInTheBanner()
    {
        TuiState cancelled = Apply(Syncing(), new SessionMsg(new JobCancelled(SessionJob.Sync)));
        cancelled.Banner!.Text.ShouldBe("Sync cancelled. It can be run again.");
        cancelled.Job.ShouldBeNull();

        TuiState refused = Apply(Press(MainScreen(), KeyInput.Of('s')).State, new Refused(Domain.Session.SessionErrors.Busy("sync")));
        refused.Running.ShouldBeNull();
        refused.Banner!.Role.ShouldBe(Role.Conflict);
    }

    [Fact]
    public void ATooSmallWindow_Should_OnlyAcceptQ()
    {
        TuiState state = Apply(MainScreen(), new Resized(79, 30));
        state.IsTooSmall.ShouldBeTrue();

        Press(state, KeyInput.Of('s')).Effects.ShouldBeEmpty();
        Press(state, KeyInput.Of('q')).Effects.ShouldBe([new Quit(0)]);
        Apply(state, new Resized(100, 30)).IsTooSmall.ShouldBeFalse();
    }

    [Fact]
    public void ResizingSmaller_Should_KeepTheHighlightInView()
    {
        TuiState state = Press(MainScreen(), Key(KeyCode.End)).State;

        state = Apply(state, new Resized(80, 24));

        (state.Selected - state.Scroll).ShouldBeLessThan(state.ListHeight);
    }

    [Fact]
    public void FileContent_Should_ReachTheScreenWithoutControlCharacters()
    {
        string text = "a" + (char)27 + "[2Jb\tc" + (char)7;

        TuiUpdate.Visible(text, ascii: false).ShouldBe("a␛[2Jb    c␇");
        TuiUpdate.Visible(text, ascii: true).ShouldBe("a?[2Jb    c?");
    }

    private static SyncReport Report() => new(
        ShareId,
        Domain.Manifests.Revision.TryCreate(43, out Domain.Manifests.Revision? revision) ? revision! : throw new InvalidOperationException(),
        Committed: true,
        DryRun: false,
        new TransferTotals(2, 10_034),
        new TransferTotals(2, 1_367_346),
        DeletedLocally: 1,
        DeletedRemotely: 1,
        [],
        [],
        [],
        Domain.Sync.MergePlanner.Plan(
            new Dictionary<SharePath, Domain.Manifests.ManifestEntry>(),
            new Dictionary<SharePath, Domain.Manifests.ManifestEntry>(),
            System.Collections.Immutable.ImmutableSortedDictionary.Create<SharePath, Domain.Manifests.ManifestEntry>(SharePath.OrdinalComparer),
            MergeOptions.Default));
}
