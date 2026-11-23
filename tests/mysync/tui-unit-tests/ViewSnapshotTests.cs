using MySync.Application.Abstractions.Progress;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Session;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Storage;
using MySync.Domain.Sync;
using MySync.Testing;
using MySync.Tui.Messages;
using MySync.Tui.State;
using MySync.Tui.Views;
using Xunit;
using static MySync.Tui.UnitTests.Fixtures;

namespace MySync.Tui.UnitTests;

/// <summary>
/// Every screen rendered to a frame and compared with its saved snapshot (text and styles), at
/// 100×30 and 80×24, in Unicode and <c>--ascii</c> (CLAUDE.md §10d). Colour depth doesn't change a
/// frame: the theme applies it when drawing (see <c>AnsiTerminalTests</c>).
/// </summary>
public class ViewSnapshotTests
{
    public static TheoryData<string, int, int, bool> Cases()
    {
        TheoryData<string, int, int, bool> cases = [];
        foreach (string name in Screens.Keys)
        {
            foreach ((int width, int height) in new[] { (100, 30), (80, 24) })
            {
                cases.Add(name, width, height, false);
                cases.Add(name, width, height, true);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Screen_Should_MatchItsSnapshot(string name, int width, int height, bool ascii)
    {
        TuiState state = Screens[name](width, height, ascii);

        Snapshot.Match(Views.Screens.Render(state), $"{name}.{width}x{height}{(ascii ? ".ascii" : string.Empty)}");
    }

    [Fact]
    public void ATooSmallWindow_Should_OnlySaySo()
    {
        Snapshot.Match(Views.Screens.Render(MainScreen(60, 20)), "too-small.60x20");
    }

    private static readonly Dictionary<string, Func<int, int, bool, TuiState>> Screens = new()
    {
        ["main-changes"] = (w, h, a) => MainScreen(w, h, a),
        ["main-folder-selected"] = (w, h, a) => Select(MainScreen(w, h, a), "deploy"),
        ["main-in-sync"] = (w, h, a) => MainScreen(w, h, a, StatusOf([Entry("README.md", FileStatus.Synced), Entry("docs/guide.md", FileStatus.Synced)], 2, CompressionSetting.None)),
        ["main-new-empty-share"] = (w, h, a) => MainScreen(w, h, a, StatusOf([Entry("a.txt", FileStatus.NewHere), Entry("docs/b.md", FileStatus.NewHere)], 0)),
        ["main-error-banner"] = (w, h, a) => Apply(Apply(MainScreen(w, h, a), new SessionMsg(new JobStarted(SessionJob.Rescan))), new SessionMsg(new JobFailed(SessionJob.Rescan, StorageErrors.Unreachable))),
        ["main-search"] = (w, h, a) => Type(Press(MainScreen(w, h, a), KeyInput.Of('/')).State, "app").State,
        ["progress-sync"] = Syncing,
        ["progress-push"] = (w, h, a) => Pushing(w, h, a),
        ["conflicts-one"] = (w, h, a) => Press(MainScreen(w, h, a), KeyInput.Of('c')).State,
        ["conflicts-none"] = (w, h, a) => Press(MainScreen(w, h, a, StatusOf([Entry("README.md", FileStatus.Synced)], 1)), KeyInput.Of('c')).State,
        ["conflicts-delete"] = (w, h, a) => Press(MainScreen(w, h, a, StatusOf([Entry("deploy/deploy.ps1", FileStatus.Conflict) with { ConflictKind = ConflictKind.ModifyDelete }], 1)), KeyInput.Of('c')).State,
        ["diff"] = (w, h, a) => Diff(w, h, a),
        ["push-empty"] = (w, h, a) => Press(Select(MainScreen(w, h, a), "deploy"), KeyInput.Of('p')).State,
        ["push-filled"] = (w, h, a) => Press(Type(Press(Select(MainScreen(w, h, a), "deploy"), KeyInput.Of('p')).State, "server-logs").State, Key(KeyCode.Tab), KeyInput.Of(' ')).State,
        ["push-invalid-name"] = (w, h, a) => Type(Press(Select(MainScreen(w, h, a), "deploy"), KeyInput.Of('p')).State, "Bad Name!").State,
        ["push-own-share"] = (w, h, a) => Type(Press(Select(MainScreen(w, h, a), "deploy"), KeyInput.Of('p')).State, "app-config").State,
        ["confirm-mass-delete"] = (w, h, a) => MassDelete(w, h, a),
        ["confirm-large-upload"] = (w, h, a) => Press(MainScreen(w, h, a, LargeNewShare()), KeyInput.Of('s')).State,
        ["confirm-quit"] = (w, h, a) => Press(Syncing(w, h, a), KeyInput.Of('q')).State,
        ["help"] = (w, h, a) => Press(MainScreen(w, h, a), KeyInput.Of('?')).State,
    };

    private static TuiState Select(TuiState state, string path) =>
        state with { Selected = state.Rows.IndexOf(state.Rows.First(row => row.Path == path)) };

    private static TuiState Pushing(int width, int height, bool ascii)
    {
        TuiState state = MainScreen(width, height, ascii);
        SessionEvent[] events =
        [
            new JobStarted(new SessionJob(JobKind.Push, "push", "deploy", "server-logs")),
            new PhaseStarted(SyncPhase.Scanning),
            new PhaseStarted(SyncPhase.Uploading),
            new TransfersPlanned(TransferDirection.Upload, 1, 5734),
            new FileStarted(TransferDirection.Upload, SharePath.Create("deploy/notes.md").Value),
            new TransferProgress(TransferDirection.Upload, 2867, 0),
        ];
        return events.Aggregate(state, (current, e) => Apply(current, new SessionMsg(e)));
    }

    private static TuiState MassDelete(int width, int height, bool ascii)
    {
        TuiState state = Apply(MainScreen(width, height, ascii), new SessionMsg(new JobStarted(SessionJob.Sync)));
        return Apply(state, new SessionMsg(new JobFailed(SessionJob.Sync, SyncErrors.MassDeleteBlocked(84, 128))));
    }

    private static FolderStatus LargeNewShare() =>
        StatusOf([.. Enumerable.Range(0, 1200).Select(i => Entry($"data/file-{i:0000}.csv", FileStatus.NewHere, 900_000))], 0);

    private static TuiState Diff(int width, int height, bool ascii)
    {
        TuiState state = Press(MainScreen(width, height, ascii), KeyInput.Of('c'), KeyInput.Of('d')).State;
        DiffSide mine = new(EntryType.File, ManifestBuilder.HashOf("mine"), 9011, Now.AddMinutes(-12), false);
        DiffSide theirs = new(EntryType.File, ManifestBuilder.HashOf("theirs"), 9318, Now.AddMinutes(-89), false, Rev42, "ali@SERVER01");
        DiffHunk hunk = new(3, 4, 3, 4,
        [
            new DiffLine(DiffLineKind.Context, "param([string]$Environment)"),
            new DiffLine(DiffLineKind.Removed, "$Servers = @('web01', 'web02')"),
            new DiffLine(DiffLineKind.Added, "$Servers = @('web01', 'web02', 'web03')"),
            new DiffLine(DiffLineKind.Added, "Write-Host \"" + (char)27 + "[31mred" + (char)27 + "[0m\"\r"),
            new DiffLine(DiffLineKind.Context, "Invoke-Deploy -Servers $Servers"),
        ]);
        PathDiff diff = new(
            SharePath.Create("deploy/deploy.ps1").Value,
            ConflictKind.ModifyModify,
            mine,
            theirs,
            null,
            [new DiffSection("Yours and theirs", DiffSideKind.Local, DiffSideKind.Remote, "local/deploy/deploy.ps1", "remote/deploy/deploy.ps1", new TextOutcome([hunk], Added: 2, Removed: 1))],
            ["Line endings differ: CRLF here, LF in the share."]);
        return Apply(state, new DiffLoaded("deploy/deploy.ps1", new DiffResponse(ShareId, [diff]), null));
    }
}
