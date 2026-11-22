using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.FileSystem;

/// <summary>What <c>mysync push</c> reads, on real temp folders (CLAUDE.md §10b).</summary>
public sealed class LocalSourcesTests : IDisposable
{
    private readonly TempShareFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LocalSources Sources => new(new ShareRoot(_temp.Path));

    private static SharePath P(string value) => SharePath.Create(value).Value;

    [Fact]
    public void Inspect_Should_TellFilesFoldersAndMissingPaths_RelativeToTheCurrentFolder()
    {
        _temp.WriteFile("logs/a.log", "a");

        LocalSourceInfo folder = Sources.Inspect("logs");
        LocalSourceInfo file = Sources.Inspect(Path.Combine("logs", "a.log"));
        LocalSourceInfo missing = Sources.Inspect("nope");
        LocalSourceInfo current = Sources.CurrentFolder();

        folder.Kind.ShouldBe(LocalSourceKind.Folder);
        folder.Name.ShouldBe("logs");
        folder.FullPath.ShouldBe(_temp.Combine("logs"));
        folder.Argument.ShouldBe("logs");
        folder.IsUnsafeRoot.ShouldBeFalse();
        file.Kind.ShouldBe(LocalSourceKind.File);
        file.Name.ShouldBe("a.log");
        missing.Kind.ShouldBe(LocalSourceKind.Missing);
        current.Kind.ShouldBe(LocalSourceKind.Folder);
        current.FullPath.ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_temp.Path)));
        Sources.Inspect(_temp.Combine("logs") + Path.DirectorySeparatorChar).Name.ShouldBe("logs");
    }

    [Fact]
    public void Inspect_Should_CallAFilesystemRootUnsafe_AndNameless()
    {
        LocalSourceInfo root = Sources.Inspect(Path.GetPathRoot(_temp.Path)!);

        root.Kind.ShouldBe(LocalSourceKind.Folder);
        root.IsUnsafeRoot.ShouldBeTrue();
        root.Name.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task List_Should_WalkFolders_LeavingOutOnlyMysyncsArtifactsAndOsClutter_AtAnyDepth()
    {
        _temp.WriteFile("app/b.log", "b")
            .WriteFile("app/.mysyncignore", "*.log")
            .WriteFile("app/.mysynctool", "{}")
            .WriteFile("app/deep/.mysynctool.lock", "")
            .WriteFile("app/deep/.DS_Store", "x")
            .WriteFile("app/deep/._b.log", "x")
            .WriteFile("app/sub/a.log", "aa")
            .CreateDirectory("app/empty")
            .WriteFile("config.yml", "c");

        SourceListing listing = (await Sources.ListAsync(
            [new SourceToList(_temp.Combine("app"), IsFolder: true, P("app")), new SourceToList(_temp.Combine("config.yml"), IsFolder: false, P("config.yml"))],
            Ct)).Value;

        listing.Files.Select(file => file.Path.Value).ShouldBe(["app/.mysyncignore", "app/b.log", "app/sub/a.log", "config.yml"]);
        listing.EmptyFolders.Select(path => path.Value).ShouldBe(["app/deep", "app/empty"]);
        listing.Skipped.ShouldBeEmpty();
        SourceFile a = listing.Files.Single(file => file.Path.Value == "app/sub/a.log");
        a.Size.ShouldBe(2);
        a.FullPath.ShouldBe(_temp.Combine(Path.Combine("app", "sub", "a.log")));
    }

    [Fact]
    public async Task List_Should_PutTheCurrentFoldersContentsAtTheTargetRoot()
    {
        _temp.WriteFile("a.log", "a").WriteFile("sub/b.log", "b");

        SourceListing listing = (await Sources.ListAsync([new SourceToList(_temp.Path, IsFolder: true, Root: null)], Ct)).Value;

        listing.Files.Select(file => file.Path.Value).ShouldBe(["a.log", "sub/b.log"]);
    }

    [Fact]
    public async Task List_Should_SkipSymlinks_NotFollowThem()
    {
        _temp.WriteFile("real/file.txt", "r").CreateDirectory("app");
        try
        {
            Directory.CreateSymbolicLink(_temp.Combine("app/dirlink"), _temp.Combine("real"));
            File.CreateSymbolicLink(_temp.Combine("app/filelink.txt"), _temp.Combine("real/file.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }

        SourceListing listing = (await Sources.ListAsync([new SourceToList(_temp.Combine("app"), IsFolder: true, P("app"))], Ct)).Value;

        listing.Files.ShouldBeEmpty();
        listing.Skipped.Select(skip => (skip.Path, skip.Reason)).ShouldBe([("app/dirlink", LocalSkipReason.Symlink), ("app/filelink.txt", LocalSkipReason.Symlink)]);
        Sources.Inspect(_temp.Combine("app/dirlink")).Kind.ShouldBe(LocalSourceKind.Symlink);
    }

    [Fact]
    public async Task Open_Should_ShareTheFileWithItsWriter_AndReportAVanishedFile()
    {
        _temp.WriteFile("app.log", "line 1\n");
        string path = _temp.Combine("app.log");
        await using FileStream writer = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        OpenedSourceFile opened = (await Sources.OpenAsync(path, Ct)).Value;
        await using (opened)
        {
            opened.Content.Length.ShouldBe(7);
            opened.Content.CanSeek.ShouldBeTrue();
            await writer.WriteAsync("line 2\n"u8.ToArray(), Ct);
            await writer.FlushAsync(Ct);
            using StreamReader reader = new(opened.Content);
            (await reader.ReadToEndAsync(Ct)).ShouldStartWith("line 1\n");
        }

        SharedKernel.Error gone = (await Sources.OpenAsync(_temp.Combine("gone.log"), Ct)).Error;
        gone.Code.ShouldBe("Local.Unreadable");
        gone.Description.ShouldContain("removed");
    }

    [Fact]
    public async Task FindSyncedFolders_Should_ReadTheShareIdOfTheFolderAndEveryParent()
    {
        var outer = ShareId.New();
        var inner = ShareId.New();
        _temp.CreateDirectory("outer/inner/deep");
        await SaveStateAsync(_temp.Combine("outer"), outer);
        await SaveStateAsync(_temp.Combine(Path.Combine("outer", "inner")), inner);

        IReadOnlyList<SyncedFolder> found = (await Sources.FindSyncedFoldersAsync(_temp.Combine(Path.Combine("outer", "inner", "deep")), Ct)).Value;

        found.ShouldBe([
            new SyncedFolder(_temp.Combine(Path.Combine("outer", "inner")), inner),
            new SyncedFolder(_temp.Combine("outer"), outer)]);
    }

    [Fact]
    public async Task FindSyncedFolders_Should_StartAtTheFolderOfAFile()
    {
        var shareId = ShareId.New();
        _temp.WriteFile("app/sub/b.log", "x");
        await SaveStateAsync(_temp.Combine("app"), shareId);

        IReadOnlyList<SyncedFolder> found = (await Sources.FindSyncedFoldersAsync(_temp.Combine(Path.Combine("app", "sub", "b.log")), Ct)).Value;

        found.ShouldBe([new SyncedFolder(_temp.Combine("app"), shareId)]);
    }

    [Fact]
    public async Task FindSyncedFolders_Should_FailOnADamagedStateFile()
    {
        _temp.WriteFile("app/.mysynctool", "{ not json");

        (await Sources.FindSyncedFoldersAsync(_temp.Combine("app"), Ct)).Error.Code.ShouldBe("State.Corrupt");
    }

    public void Dispose() => _temp.Dispose();

    private static async Task SaveStateAsync(string folder, ShareId shareId)
    {
        var state = ShareState.Create(new StorageLocation("azure-blob", "acct", "mysync"), shareId, Domain.Manifests.Revision.Initial, [], [], []);
        (await new JsonShareStateStore(new ShareRoot(folder)).SaveAsync(state, Ct)).IsSuccess.ShouldBeTrue();
    }
}
