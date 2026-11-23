using System.Text;
using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.State;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Testing.Contracts;

/// <summary>
/// The behaviour every <see cref="ILocalFolder"/> must have, which the sync pipeline relies on.
/// Derive once per implementation: <see cref="InMemoryLocalFolder"/> in the application unit
/// tests, the real disk folder in the infrastructure tests. If they disagree, the fake is wrong.
/// </summary>
public abstract class LocalFolderContractTests
{
    private static readonly DateTimeOffset Mtime = new(2026, 11, 15, 18, 31, 2, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Whether this implementation can store the executable bit (false on Windows).</summary>
    protected abstract bool SupportsExecutableBit { get; }

    /// <summary>A fresh, empty folder.</summary>
    protected abstract Task<ILocalFolder> CreateFolderAsync();

    /// <summary>Test setup that bypasses the interface: put a file there directly.</summary>
    protected abstract Task SeedFileAsync(ILocalFolder folder, string path, byte[] content);

    /// <summary>Test setup that bypasses the interface: create a directory directly.</summary>
    protected abstract Task SeedDirectoryAsync(ILocalFolder folder, string path);

    protected abstract Task<byte[]?> ReadFileAsync(ILocalFolder folder, string path);

    protected abstract Task<bool> IsDirectoryAsync(ILocalFolder folder, string path);

    protected abstract Task<bool> IsExecutableAsync(ILocalFolder folder, string path);

    [Fact]
    public async Task NewFolder_Should_BeEmpty()
    {
        ILocalFolder folder = await CreateFolderAsync();

        (await UserNames(folder)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetEntryKind_Should_TellNothingAFileAndADirectory()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "dir/file.txt", Encoding.UTF8.GetBytes("x"));
        await SeedDirectoryAsync(folder, "empty");

        (await folder.GetEntryKindAsync(P("dir/file.txt"), Ct)).Value.ShouldBe(LocalEntryKind.File);
        (await folder.GetEntryKindAsync(P("dir"), Ct)).Value.ShouldBe(LocalEntryKind.Directory);
        (await folder.GetEntryKindAsync(P("empty"), Ct)).Value.ShouldBe(LocalEntryKind.Directory);
        (await folder.GetEntryKindAsync(P("missing"), Ct)).Value.ShouldBe(LocalEntryKind.None);
        (await folder.GetEntryKindAsync(P("missing/below"), Ct)).Value.ShouldBe(LocalEntryKind.None);
    }

    [Fact]
    public async Task ListTopLevelNames_Should_ListOnlyTheRootsChildren()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a.txt", Bytes("a"));
        await SeedFileAsync(folder, "docs/readme.md", Bytes("r"));
        await SeedDirectoryAsync(folder, "empty");

        (await UserNames(folder)).Order(StringComparer.Ordinal).ShouldBe(["a.txt", "docs", "empty"]);
    }

    [Fact]
    public async Task GetFile_Should_ReturnSize_ForAFile_AndNull_ForAMissingPathOrADirectory()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a.txt", Bytes("12345"));
        await SeedDirectoryAsync(folder, "dir");

        LocalFile file = Unwrap(await folder.GetFileAsync(P("a.txt"), Ct))!;
        file.Size.ShouldBe(5);
        file.Path.ShouldBe(P("a.txt"));
        Unwrap(await folder.GetFileAsync(P("missing"), Ct)).ShouldBeNull();
        Unwrap(await folder.GetFileAsync(P("dir"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task OpenRead_Should_ReturnASeekableStreamThatReportsItsLength()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a.txt", Bytes("hello"));

        await using Stream stream = Unwrap(await folder.OpenReadAsync(P("a.txt"), Ct));

        stream.CanSeek.ShouldBeTrue();
        stream.Length.ShouldBe(5);
        (await ReadToEnd(stream)).ShouldBe(Bytes("hello"));
        stream.Position = 0;
        (await ReadToEnd(stream)).ShouldBe(Bytes("hello"));
    }

    [Fact]
    public async Task OpenRead_Should_Fail_ForAMissingFile()
    {
        ILocalFolder folder = await CreateFolderAsync();

        (await folder.OpenReadAsync(P("missing"), Ct)).Error.Code.ShouldBe("Local.Unreadable");
    }

    [Fact]
    public async Task PlaceStagedFile_Should_CreateParents_AndApplyBytesAndMtime()
    {
        ILocalFolder folder = await CreateFolderAsync();

        Unwrap(await Place(folder, "deep/er/file.txt", Bytes("content"), executable: false));

        (await ReadFileAsync(folder, "deep/er/file.txt")).ShouldBe(Bytes("content"));
        (await IsDirectoryAsync(folder, "deep/er")).ShouldBeTrue();
        Unwrap(await folder.GetFileAsync(P("deep/er/file.txt"), Ct))!.MtimeUtc.ShouldBe(Mtime);
    }

    [Fact]
    public async Task PlaceStagedFile_Should_ApplyTheExecutableBit_WhereSupported()
    {
        ILocalFolder folder = await CreateFolderAsync();

        Unwrap(await Place(folder, "run.sh", Bytes("#!/bin/sh"), executable: true));

        (await IsExecutableAsync(folder, "run.sh")).ShouldBe(SupportsExecutableBit);
    }

    [Fact]
    public async Task PlaceStagedFile_Should_ReplaceAnExistingFile()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a.txt", Bytes("old"));

        Unwrap(await Place(folder, "a.txt", Bytes("new"), executable: false));

        (await ReadFileAsync(folder, "a.txt")).ShouldBe(Bytes("new"));
    }

    [Fact]
    public async Task PlaceStagedFile_Should_ReplaceAnEmptyDirectory()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedDirectoryAsync(folder, "x");

        Unwrap(await Place(folder, "x", Bytes("now a file"), executable: false));

        (await IsDirectoryAsync(folder, "x")).ShouldBeFalse();
        (await ReadFileAsync(folder, "x")).ShouldBe(Bytes("now a file"));
    }

    [Fact]
    public async Task PlaceStagedFile_Should_Fail_OnANonEmptyDirectory_AndLeaveItAlone()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "x/inside.txt", Bytes("keep me"));

        (await Place(folder, "x", Bytes("file"), executable: false)).Error.Code.ShouldBe("Local.WriteFailed");

        (await ReadFileAsync(folder, "x/inside.txt")).ShouldBe(Bytes("keep me"));
    }

    [Fact]
    public async Task PlaceStagedFile_Should_Fail_WhenAParentIsAFile()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a", Bytes("file"));

        (await Place(folder, "a/b", Bytes("x"), executable: false)).Error.Code.ShouldBe("Local.WriteFailed");

        (await ReadFileAsync(folder, "a")).ShouldBe(Bytes("file"));
    }

    [Fact]
    public async Task StagedFile_DisposedWithoutPlacing_Should_LeaveNothingVisible()
    {
        ILocalFolder folder = await CreateFolderAsync();

        await using (IStagedFile staged = Unwrap(await folder.CreateStagedFileAsync(Ct)))
        {
            await staged.Content.WriteAsync(Bytes("abandoned"), Ct);
        }

        (await UserNames(folder)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CleanStaging_Should_DiscardStagedFiles()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await using IStagedFile staged = Unwrap(await folder.CreateStagedFileAsync(Ct));
        await staged.Content.WriteAsync(Bytes("x"), Ct);
        await staged.Content.FlushAsync(Ct);

        Unwrap(await folder.CleanStagingAsync(Ct));

        (await folder.PlaceStagedFileAsync(staged, P("x"), Mtime, executable: false, Ct)).IsFailure.ShouldBeTrue();
        (await UserNames(folder)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SetExecutable_Should_ToggleTheBit_WhereSupported_AndFail_ForAMissingFile()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "run.sh", Bytes("#!/bin/sh"));

        Unwrap(await folder.SetExecutableAsync(P("run.sh"), executable: true, Ct));
        (await IsExecutableAsync(folder, "run.sh")).ShouldBe(SupportsExecutableBit);
        Unwrap(await folder.SetExecutableAsync(P("run.sh"), executable: false, Ct));
        (await IsExecutableAsync(folder, "run.sh")).ShouldBeFalse();

        (await folder.SetExecutableAsync(P("missing"), executable: true, Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateDirectory_Should_CreateParents_AndBeIdempotent()
    {
        ILocalFolder folder = await CreateFolderAsync();

        Unwrap(await folder.CreateDirectoryAsync(P("a/b/c"), Ct));
        Unwrap(await folder.CreateDirectoryAsync(P("a/b/c"), Ct));

        (await IsDirectoryAsync(folder, "a/b/c")).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateDirectory_Should_Fail_WhereAFileIs()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a", Bytes("file"));

        (await folder.CreateDirectoryAsync(P("a"), Ct)).Error.Code.ShouldBe("Local.WriteFailed");
        (await ReadFileAsync(folder, "a")).ShouldBe(Bytes("file"));
    }

    [Fact]
    public async Task DeleteFile_Should_RemoveIt_SucceedWhenMissing_AndRefuseADirectory()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedFileAsync(folder, "a.txt", Bytes("a"));
        await SeedDirectoryAsync(folder, "dir");

        Unwrap(await folder.DeleteFileAsync(P("a.txt"), Ct));
        (await ReadFileAsync(folder, "a.txt")).ShouldBeNull();
        (await folder.DeleteFileAsync(P("a.txt"), Ct)).IsSuccess.ShouldBeTrue();
        (await folder.DeleteFileAsync(P("dir"), Ct)).IsFailure.ShouldBeTrue();
        (await IsDirectoryAsync(folder, "dir")).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteDirectoryIfEmpty_Should_DeleteOnlyEmptyDirectories()
    {
        ILocalFolder folder = await CreateFolderAsync();
        await SeedDirectoryAsync(folder, "empty");
        await SeedFileAsync(folder, "full/file.txt", Bytes("x"));
        await SeedFileAsync(folder, "file.txt", Bytes("x"));

        Unwrap(await folder.DeleteDirectoryIfEmptyAsync(P("empty"), Ct)).ShouldBeTrue();
        (await IsDirectoryAsync(folder, "empty")).ShouldBeFalse();
        Unwrap(await folder.DeleteDirectoryIfEmptyAsync(P("full"), Ct)).ShouldBeFalse();
        (await IsDirectoryAsync(folder, "full")).ShouldBeTrue();
        Unwrap(await folder.DeleteDirectoryIfEmptyAsync(P("missing"), Ct)).ShouldBeFalse();
        Unwrap(await folder.DeleteDirectoryIfEmptyAsync(P("file.txt"), Ct)).ShouldBeFalse();
        (await ReadFileAsync(folder, "file.txt")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Paths_Should_BeNfc_SoADecomposedNameFindsTheComposedFile()
    {
        ILocalFolder folder = await CreateFolderAsync();

        Unwrap(await Place(folder, "café.txt", Bytes("x"), executable: false));

        Unwrap(await folder.GetFileAsync(P("café.txt"), Ct)).ShouldNotBeNull();
    }

    private static async Task<Result> Place(ILocalFolder folder, string path, byte[] content, bool executable)
    {
        await using IStagedFile staged = Unwrap(await folder.CreateStagedFileAsync(Ct));
        await staged.Content.WriteAsync(content, Ct);
        await staged.Content.FlushAsync(Ct);
        return await folder.PlaceStagedFileAsync(staged, P(path), Mtime, executable, Ct);
    }

    // Top-level names minus mysync's own artifacts (e.g. the staging directory).
    private static async Task<List<string>> UserNames(ILocalFolder folder) =>
        [.. Unwrap(await folder.ListTopLevelNamesAsync(Ct)).Where(name => !StateFileNames.AlwaysIgnored.Contains(name))];

    private static async Task<byte[]> ReadToEnd(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static void Unwrap(Result result) =>
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);

    private static T Unwrap<T>(Result<T> result)
    {
        Unwrap((Result)result);
        return result.Value;
    }
}
