using System.Text;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Application.UnitTests.Sync.RunSyncTests;

namespace MySync.Application.UnitTests.Conflicts;

/// <summary><c>mysync diff</c> (CLAUDE.md §10): what differs, to decide a conflict. Read-only.</summary>
public sealed class DiffTests
{
    private const string Base = "region,quarter,total\nnorth,q3,1200\nsouth,q3,980\neast,q3,1430\n";

    [Fact]
    public async Task ModifyModify_Should_ShowLocalAgainstTheRecordedRemote()
    {
        (_, _, Machine b, _) = await Conflicted(Base.Replace("980", "1010", StringComparison.Ordinal), Base.Replace("980", "999", StringComparison.Ordinal));

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Kind.ShouldBe(ConflictKind.ModifyModify);
        item.Notes.ShouldBeEmpty();
        item.Remote!.UpdatedBy.ShouldBe("usman@DEV-LAPTOP");
        DiffSection section = item.Sections.Single();
        (section.OldLabel, section.NewLabel).ShouldBe(("local/q3.csv", "remote/q3.csv"));
        TextOutcome text = section.Outcome.ShouldBeOfType<TextOutcome>();
        (text.Added, text.Removed).ShouldBe((1, 1));
        text.Hunks.Single().Lines.Where(line => line.Kind != DiffLineKind.Context).Select(line => line.Op + line.Text)
            .ShouldBe(["-south,q3,999", "+south,q3,1010"]);
    }

    [Fact]
    public async Task Base_Should_ShowYourChangesAndTheirChanges()
    {
        (_, _, Machine b, _) = await Conflicted(Base.Replace("1200", "1300", StringComparison.Ordinal), Base.Replace("1430", "1500", StringComparison.Ordinal));

        DiffSection[] sections = [.. (await b.DiffAsync(new DiffQuery([], AgainstBase: true))).ShouldSucceed().Items.Single().Sections];

        sections.Select(section => (section.Title, section.OldLabel, section.NewLabel)).ShouldBe(
            [("your changes", "base/q3.csv", "local/q3.csv"), ("their changes", "base/q3.csv", "remote/q3.csv")]);
        Changed(sections[0]).ShouldBe(["-east,q3,1430", "+east,q3,1500"]);
        Changed(sections[1]).ShouldBe(["-north,q3,1200", "+north,q3,1300"]);
    }

    [Fact]
    public async Task AddAdd_Should_ShowLocalAgainstRemote()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("other.txt", "o"));
        a.Folder.WriteFile("new.txt", "from a\n");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("new.txt", "from b\n");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.AddAdd);

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Base.ShouldBeNull();
        Changed(item.Sections.Single()).ShouldBe(["-from b", "+from a"]);
    }

    [Fact]
    public async Task ModifyDelete_Should_ShowYourChanges_AndSayTheShareDeletedIt()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("q3.csv", Base), ("other.txt", "o"));
        a.Folder.Remove("q3.csv");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("q3.csv", Base + "west,q3,10\n");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyDelete);

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Remote.ShouldBeNull();
        item.Notes.ShouldContain(DiffQueryHandler.ShareDeletedNote);
        DiffSection section = item.Sections.Single();
        (section.Title, section.OldLabel, section.NewLabel).ShouldBe(("your changes", "base/q3.csv", "local/q3.csv"));
        Changed(section).ShouldBe(["+west,q3,10"]);
    }

    [Fact]
    public async Task DeleteModify_Should_ShowTheirChanges_AndSayYouDeletedIt()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("q3.csv", Base), ("other.txt", "o"));
        a.Folder.WriteFile("q3.csv", Base + "west,q3,10\n");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.Remove("q3.csv");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.DeleteModify);

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Local.ShouldBeNull();
        item.Notes.ShouldContain(DiffQueryHandler.YouDeletedNote);
        DiffSection section = item.Sections.Single();
        (section.Title, section.OldLabel, section.NewLabel).ShouldBe(("their changes", "base/q3.csv", "remote/q3.csv"));
        Changed(section).ShouldBe(["+west,q3,10"]);
    }

    [Fact]
    public async Task ATypeConflict_Should_BeDescribed_NotDiffed()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("x", "file"), ("other.txt", "o"));
        a.Folder.Remove("x");
        a.Folder.WriteFile("x/inside.txt", "now a folder");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("x", "edited file");
        SyncReport report = (await b.SyncAsync()).ShouldSucceed();
        Conflict conflict = report.Conflicts.First(open => open.Kind == ConflictKind.Type);

        PathDiff item = (await b.DiffAsync(new DiffQuery([conflict.Path.Value]))).ShouldSucceed().Items.Single();

        item.Sections.Single().Outcome.ShouldBeOfType<TypeMismatchOutcome>().Description.ShouldContain("here");
    }

    [Fact]
    public async Task APathNotInConflict_Should_BeComparedWithHead()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("notes.txt", "one\ntwo\n"), ("other.txt", "o"));
        a.Folder.WriteFile("notes.txt", "one\nTWO\n");
        (await a.SyncAsync()).ShouldSucceed();

        PathDiff item = (await b.DiffAsync(new DiffQuery(["notes.txt"]))).ShouldSucceed().Items.Single();

        item.Kind.ShouldBeNull();
        item.Remote!.Revision.ShouldBe((await world.HeadAsync(shareId)).Revision);
        Changed(item.Sections.Single()).ShouldBe(["-two", "+TWO"]);
    }

    [Fact]
    public async Task ALocalOnlyFile_Should_BeComparedWithNothing()
    {
        (_, _, Machine b, _) = await TwoMachines(("other.txt", "o"));
        b.Folder.WriteFile("draft.txt", "new\n");

        DiffSection section = (await b.DiffAsync(new DiffQuery(["draft.txt"]))).ShouldSucceed().Items.Single().Sections.Single();

        (section.OldLabel, section.NewLabel).ShouldBe(("local/draft.txt", "/dev/null"));
        Changed(section).ShouldBe(["-new"]);
    }

    [Fact]
    public async Task APathInNeitherPlace_Should_BePathNotFound()
    {
        (_, _, Machine b, _) = await TwoMachines(("other.txt", "o"));

        Error error = (await b.DiffAsync(new DiffQuery(["nope.txt"]))).ShouldFail();

        error.Code.ShouldBe("Diff.PathNotFound");
        error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public async Task ALocalEditAfterTheConflict_Should_BeNoted_AndShown()
    {
        (_, _, Machine b, _) = await Conflicted("a's\n", "b's\n");
        b.Folder.WriteFile("q3.csv", "b's again\n");

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Notes.ShouldContain(DiffQueryHandler.LocalChangedNote);
        Changed(item.Sections.Single()).ShouldBe(["-b's again", "+a's"]);
    }

    [Fact]
    public async Task ARemoteEditAfterTheConflict_Should_BeNoted_ButTheRecordedRemoteShown()
    {
        (_, Machine a, Machine b, _) = await Conflicted("a's\n", "b's\n");
        a.Folder.WriteFile("q3.csv", "a's again\n");
        (await a.SyncAsync()).ShouldSucceed();

        PathDiff item = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();

        item.Notes.ShouldBe([DiffQueryHandler.RemoteChangedNote]);
        Changed(item.Sections.Single()).ShouldBe(["-b's", "+a's"]);
    }

    [Fact]
    public async Task AVersionTheShareNoLongerStores_Should_BeVersionNotStored()
    {
        (SyncWorld world, _, Machine b, ShareId shareId) = await Conflicted("a's\n", "b's\n");
        (await world.Store.DeleteObjectAsync(shareId, ManifestBuilder.HashOf("a's\n"), SyncWorld.Ct)).IsSuccess.ShouldBeTrue();

        Error error = (await b.DiffAsync(new DiffQuery([]))).ShouldFail();

        error.Code.ShouldBe("Diff.VersionNotStored");
        error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task AStoredVersionThatDoesntMatchItsHash_Should_BeRefused()
    {
        (SyncWorld world, _, Machine b, _) = await Conflicted("a's\n", "b's\n");
        world.Store.DownloadStream = (_, _) => new MemoryStream(Encoding.UTF8.GetBytes("tampered\n"));

        (await b.DiffAsync(new DiffQuery([]))).ShouldFail().Code.ShouldBe("Storage.HashMismatch");
    }

    [Fact]
    public async Task BinaryFiles_Should_NotBeDiffed()
    {
        (_, _, Machine b, _) = await Conflicted("a\0bin", "b\0bin");

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single().Sections.Single().Outcome.ShouldBeOfType<BinaryOutcome>();
    }

    [Fact]
    public async Task FilesOverTheLimit_Should_BeTooLarge_WithoutBeingBuffered()
    {
        string big = new('a', DiffLimits.MaxBytes + 10);
        (_, _, Machine b, _) = await Conflicted(big + "a", big + "b");

        TooLargeOutcome outcome = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single().Sections.Single().Outcome.ShouldBeOfType<TooLargeOutcome>();

        outcome.OldSize.ShouldBe(DiffLimits.MaxBytes + 11);
        outcome.NewSize.ShouldBe(DiffLimits.MaxBytes + 11);
    }

    [Fact]
    public async Task OnlyLineEndings_Should_SaySo()
    {
        (_, _, Machine b, _) = await Conflicted("a\nb\n", "a\r\nb\r\n");

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single().Sections.Single().Outcome
            .ShouldBe(new EolOnlyOutcome(LineEndingStyle.CrLf, LineEndingStyle.Lf));
    }

    [Fact]
    public async Task OnlyTheExecutableBit_Should_SaySo()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("run.sh", "echo 1\n"), ("other.txt", "o"));
        a.Folder.WriteFile("run.sh", "echo 2\n", executable: true);
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("run.sh", "echo 2\n");
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single().Sections.Single().Outcome
            .ShouldBe(new ExecOnlyOutcome(OldExecutable: false, NewExecutable: true));
    }

    [Fact]
    public async Task Stat_Should_CountWithoutHunks()
    {
        (_, _, Machine b, _) = await Conflicted("x\ny\nz\n", "x\nY\nZ\nw\n");

        TextOutcome text = (await b.DiffAsync(new DiffQuery([], StatOnly: true))).ShouldSucceed().Items.Single().Sections.Single().Outcome.ShouldBeOfType<TextOutcome>();

        text.Hunks.ShouldBeEmpty();
        (text.Added, text.Removed).ShouldBe((2, 3));
    }

    [Fact]
    public async Task NoPaths_Should_ListEveryOpenConflict_InPathOrder()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("z.txt", "z"), ("a.txt", "a"), ("m.txt", "m"));
        foreach (string path in new[] { "z.txt", "a.txt" })
        {
            a.Folder.WriteFile(path, "a's");
            b.Folder.WriteFile(path, "b's");
        }

        (await a.SyncAsync()).ShouldSucceed();
        (await b.SyncAsync()).ShouldSucceed();

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Select(item => item.Path.Value).ShouldBe(["a.txt", "z.txt"]);
    }

    [Fact]
    public async Task Context_Should_BeValidated()
    {
        (_, _, Machine b, _) = await TwoMachines(("other.txt", "o"));

        Error error = (await b.DiffAsync(new DiffQuery([], Context: DiffLimits.MaxContext + 1))).ShouldFail();

        error.ShouldBeOfType<ValidationError>().Errors.Single().Code.ShouldBe("Diff.InvalidContext");
    }

    [Theory]
    [InlineData(DiffSideKind.Local, "b's\n")]
    [InlineData(DiffSideKind.Remote, "a's\n")]
    [InlineData(DiffSideKind.Base, Base)]
    public async Task Print_Should_StreamThatVersionsBytes(DiffSideKind side, string expected)
    {
        (_, _, Machine b, _) = await Conflicted("a's\n", "b's\n");

        await using Stream stream = (await b.PrintAsync("q3.csv", side)).ShouldSucceed();
        using StreamReader reader = new(stream);

        (await reader.ReadToEndAsync(SyncWorld.Ct)).ShouldBe(expected);
    }

    [Fact]
    public async Task Print_Should_FailAtTheEnd_WhenTheStoredBytesDontMatch()
    {
        (SyncWorld world, _, Machine b, _) = await Conflicted("a's\n", "b's\n");
        world.Store.DownloadStream = (_, _) => new MemoryStream(Encoding.UTF8.GetBytes("tampered\n"));

        await using Stream stream = (await b.PrintAsync("q3.csv", DiffSideKind.Remote)).ShouldSucceed();

        await Should.ThrowAsync<IOException>(async () => await stream.CopyToAsync(Stream.Null, SyncWorld.Ct));
    }

    [Fact]
    public async Task PrintingAMissingVersion_Should_SaySo()
    {
        (_, Machine a, Machine b, _) = await TwoMachines(("q3.csv", Base), ("other.txt", "o"));
        a.Folder.Remove("q3.csv");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("q3.csv", "mine\n");
        (await b.SyncAsync()).ShouldSucceed();

        (await b.PrintAsync("q3.csv", DiffSideKind.Remote)).ShouldFail().Code.ShouldBe("Diff.VersionMissing");
    }

    [Fact]
    public async Task Diff_Should_WriteNothing_Anywhere()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await Conflicted("a's\n", "b's\n");
        a.Folder.WriteFile("other.txt", "changed");
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("local-only.txt", "l\n");

        int saves = b.State.Saves;
        int locks = b.Lock.Acquisitions;
        int logLines = b.ConflictLog.Lines.Count;
        string[] blobs = [.. world.Store.BlobNames];
        string headETag = (await world.Store.GetManifestAsync(shareId, SyncWorld.Ct)).Value!.ETag;
        string[] files = [.. b.Folder.AllPaths];
        List<FolderOperation> operations = [];
        b.Folder.OnOperation = (operation, _) =>
        {
            operations.Add(operation);
            return null;
        };

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed();
        (await b.DiffAsync(new DiffQuery(["other.txt", "local-only.txt"], AgainstBase: true))).ShouldSucceed();
        await using (Stream printed = (await b.PrintAsync("q3.csv", DiffSideKind.Remote)).ShouldSucceed())
        {
            await printed.CopyToAsync(Stream.Null, SyncWorld.Ct);
        }

        b.State.Saves.ShouldBe(saves);
        b.Lock.Acquisitions.ShouldBe(locks);
        b.ConflictLog.Lines.Count.ShouldBe(logLines);
        world.Store.BlobNames.ShouldBe(blobs);
        (await world.Store.GetManifestAsync(shareId, SyncWorld.Ct)).Value!.ETag.ShouldBe(headETag);
        b.Folder.AllPaths.ShouldBe(files);
        operations.ShouldNotBeEmpty();
        operations.ShouldAllBe(operation => operation == FolderOperation.GetFile || operation == FolderOperation.OpenRead);
    }

    /// <summary>A modify/modify conflict on <c>q3.csv</c>, seen by machine B (which synced second).</summary>
    private static async Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> Conflicted(string aEdit, string bEdit)
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await TwoMachines(("q3.csv", Base), ("other.txt", "o"));
        a.Folder.WriteFile("q3.csv", aEdit);
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("q3.csv", bEdit);
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);
        return (world, a, b, shareId);
    }

    private static string[] Changed(DiffSection section) =>
        [.. section.Outcome.ShouldBeOfType<TextOutcome>().Hunks.SelectMany(hunk => hunk.Lines).Where(line => line.Kind != DiffLineKind.Context).Select(line => line.Op + line.Text)];
}
