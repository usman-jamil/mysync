using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Session;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Testing;
using MySync.Tui.Effects;
using MySync.Tui.Messages;
using MySync.Tui.Rendering;
using MySync.Tui.State;
using MySync.Tui.Update;
using Shouldly;
using Xunit;

namespace MySync.Tui.UnitTests;

/// <summary>States like the agreed mocks (a share <c>app-config</c> at revision 42), built without a session.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 11, 10, 0, TimeSpan.Zero);

    public static readonly ShareId ShareId = ShareId.TryParse("0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10", out ShareId? id) ? id! : throw new InvalidOperationException();

    public static readonly Revision Rev42 = Revision.TryCreate(42, out Revision? revision) ? revision! : throw new InvalidOperationException();

    public static TuiOptions Options(bool ascii = false) =>
        new(ascii, ColorDepth.TrueColor, @"C:\deploy\config", "APP-SRV-02", "usman@APP-SRV-02", LargeFileCount: 1000, LargeTotalBytes: 1L << 30);

    public static FileEntry File(string content, long size, DateTimeOffset mtime) =>
        FileEntry.Create(ManifestBuilder.HashOf(content), size, mtime, executable: false).Value;

    public static FolderEntry Entry(string path, FileStatus status, long? size = 1024, DateTimeOffset? mtime = null, string? detail = null) => new(
        path,
        IsFolder: false,
        status,
        size,
        mtime ?? Now.AddMinutes(-8),
        status is FileStatus.NewInShare or FileStatus.ChangedInShare or FileStatus.DeletedInShare or FileStatus.Conflict ? "ali@SERVER01" : null,
        status is FileStatus.NewInShare or FileStatus.ChangedInShare or FileStatus.DeletedInShare or FileStatus.Conflict ? Rev42 : null,
        status == FileStatus.Conflict ? ConflictKind.ModifyModify : null,
        detail);

    /// <summary>The main mock's folder: two changes here, two in the share, two deletes, a conflict, a skip, an ignore, synced files.</summary>
    public static FolderStatus Status(params FolderEntry[] extra)
    {
        FolderEntry[] entries =
        [
            Entry("config/app.yml", FileStatus.ChangedHere, 4300, Now.AddMinutes(-8)),
            Entry("config/app.staging.yml", FileStatus.ChangedInShare, 4198),
            Entry("config/secrets.template.env", FileStatus.Synced, 612),
            Entry("deploy/deploy.ps1", FileStatus.Conflict, 9011),
            Entry("deploy/notes.md", FileStatus.NewHere, 5734, Now.AddMinutes(-23)),
            Entry("logs/install-2026-11-22.log", FileStatus.NewInShare, 1_363_148),
            Entry("old-readme.txt", FileStatus.DeletedHere, 2048),
            Entry("retired.cfg", FileStatus.DeletedInShare, 300),
            Entry("cache/build.tmp", FileStatus.Ignored, 77, detail: "matched by .mysyncignore"),
            Entry("data/locked.db", FileStatus.Skipped, 8192, detail: "it is in use by another program"),
            Entry("README.md", FileStatus.Synced, 1500),
            Entry("LICENSE", FileStatus.Synced, 1100),
            Entry("scripts/run.sh", FileStatus.Synced, 220),
            .. extra,
        ];
        return StatusOf([.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)], shareEntries: 11);
    }

    public static FolderStatus StatusOf(ImmutableArray<FolderEntry> entries, int shareEntries, CompressionSetting compression = CompressionSetting.Gzip)
    {
        ImmutableArray<Conflict> conflicts = [.. entries.Where(entry => entry.Status == FileStatus.Conflict).Select(entry => new Conflict(
            SharePath.Create(entry.Path).Value,
            entry.ConflictKind ?? ConflictKind.ModifyModify,
            File("base", 8000, Now.AddDays(-1)),
            entry.ConflictKind == ConflictKind.DeleteModify ? null : File("mine", 9011, Now.AddMinutes(-12)),
            entry.ConflictKind == ConflictKind.ModifyDelete ? null : File("theirs", 9318, Now.AddMinutes(-89)),
            Rev42,
            "ali@SERVER01",
            Now,
            Now))];

        return new FolderStatus(
            ShareId,
            [ShareName.Create("app-config").Value],
            new StorageLocation("azure-blob", "teamstore", "mysync"),
            @"C:\deploy\config",
            Rev42,
            "ali@SERVER01",
            Now.AddHours(-1),
            compression,
            shareEntries,
            entries,
            conflicts,
            [],
            ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
            [.. conflicts.Select(conflict => conflict.Path.Value)]);
    }

    /// <summary>A state after the first rescan finished two minutes ago.</summary>
    public static TuiState MainScreen(int width = 100, int height = 30, bool ascii = false, FolderStatus? status = null)
    {
        TuiState state = TuiUpdate.Initial(Options(ascii), width, height, Now.AddMinutes(-2));
        state = Apply(state, new SessionMsg(new JobStarted(SessionJob.Rescan)));
        state = Apply(state, new SessionMsg(new JobFinished(SessionJob.Rescan, new JobResult(Status: status ?? Status()))));
        return Apply(state, new Tick(Now));
    }

    public static TuiState Apply(TuiState state, Msg message) => TuiUpdate.Update(state, message).State;

    public static (TuiState State, ImmutableArray<Effect> Effects) Press(TuiState state, params KeyInput[] keys)
    {
        ImmutableArray<Effect> effects = [];
        foreach (KeyInput key in keys)
        {
            (state, ImmutableArray<Effect> more) = TuiUpdate.Update(state, new KeyPressed(key));
            effects = effects.AddRange(more);
        }

        return (state, effects);
    }

    public static (TuiState State, ImmutableArray<Effect> Effects) Type(TuiState state, string text) =>
        Press(state, [.. text.Select(KeyInput.Of)]);

    public static KeyInput Key(KeyCode code, bool shift = false) => KeyInput.Of(code, shift);

    /// <summary>A sync in progress: planned both ways, part uploaded, one download running.</summary>
    public static TuiState Syncing(int width = 100, int height = 30, bool ascii = false)
    {
        TuiState state = MainScreen(width, height, ascii);
        SessionEvent[] events =
        [
            new JobStarted(SessionJob.Sync),
            new PhaseStarted(SyncPhase.Uploading),
            new TransfersPlanned(TransferDirection.Upload, 2, 10_034),
            new FileStarted(TransferDirection.Upload, SharePath.Create("config/app.yml").Value),
            new TransferProgress(TransferDirection.Upload, 4300, 0),
            new FileFinished(TransferDirection.Upload, SharePath.Create("config/app.yml").Value),
            new FileStarted(TransferDirection.Upload, SharePath.Create("deploy/notes.md").Value),
            new TransferProgress(TransferDirection.Upload, 6246, 51_200),
            new PhaseStarted(SyncPhase.Downloading),
            new TransfersPlanned(TransferDirection.Download, 2, 1_367_346),
            new FileStarted(TransferDirection.Download, SharePath.Create("logs/install-2026-11-22.log").Value),
            new TransferProgress(TransferDirection.Download, 419_840, 2_516_582),
        ];
        return events.Aggregate(state, (current, e) => Apply(current, new SessionMsg(e)));
    }
}

/// <summary>
/// Saved screens: a frame's text and its styles compared with <c>Snapshots/&lt;name&gt;.txt</c>. A
/// missing snapshot is written and the test fails, so a new one is reviewed before it counts;
/// <c>MYSYNC_UPDATE_SNAPSHOTS=1</c> rewrites them all.
/// </summary>
internal static class Snapshot
{
    public static void Match(Frame frame, string name, [CallerFilePath] string caller = "")
    {
        string directory = Path.Combine(Path.GetDirectoryName(caller)!, "Snapshots");
        string path = Path.Combine(directory, name + ".txt");
        string cursor = frame.Cursor is { } at ? $"cursor {at.X},{at.Y}\n" : "cursor hidden\n";
        string actual = frame.ToText() + "--- styles (foreground | background; upper case is bold) ---\n" + frame.ToRoles() + cursor;

        bool exists = System.IO.File.Exists(path);
        if (!exists || Environment.GetEnvironmentVariable("MYSYNC_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(path, actual);
            if (!exists)
            {
                Assert.Fail($"Snapshot {name} was created; review {path} and run the tests again.");
            }

            return;
        }

        System.IO.File.ReadAllText(path).ReplaceLineEndings("\n").ShouldBe(actual, $"snapshot {name}");
    }
}
