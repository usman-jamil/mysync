using System.Runtime.Versioning;
using System.Text;
using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.FileSystem;

/// <summary>What the real folder does beyond the shared contract: disk-only cases (CLAUDE.md §8).</summary>
public sealed class LocalFolderTests : IDisposable
{
    private static readonly DateTimeOffset Mtime = new(2026, 11, 15, 18, 31, 2, TimeSpan.Zero);

    private readonly TempShareFolder _temp = new();
    private readonly List<TempShareFolder> _others = [];
    private readonly LocalFolder _folder;

    public LocalFolderTests() => _folder = new LocalFolder(new ShareRoot(_temp.Path));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANameStoredDecomposedOnDisk_Should_BeFoundByItsComposedPath_NotDuplicated()
    {
        // A Mac tool (or HFS+) wrote the name as NFD; manifests always say NFC.
        string decomposed = "café";
        _temp.WriteFile($"{decomposed}/notes.txt", "from disk");

        LocalFile? file = (await _folder.GetFileAsync(P("café/notes.txt"), Ct)).Value;
        file.ShouldNotBeNull();
        file.Size.ShouldBe(9);

        (await Place("café/notes.txt", "replaced")).IsSuccess.ShouldBeTrue();
        Directory.GetDirectories(_temp.Path).Select(Path.GetFileName).Count(name => name != StateFileNames.StagingDirectory).ShouldBe(1);
        (await File.ReadAllTextAsync(Path.Combine(_temp.Path, decomposed, "notes.txt"), Ct)).ShouldBe("replaced");
    }

    [Fact]
    public async Task ASymlinkedDirectoryInThePath_Should_BeRefused_SoNothingIsWrittenOutsideTheRoot()
    {
        TempShareFolder outside = new();
        _others.Add(outside);
        CreateDirectoryLink("escape", outside.Path);

        Result placed = await Place("escape/planted.txt", "x");

        placed.Error.Code.ShouldBe("Local.WriteFailed");
        placed.Error.Description.ShouldContain("symlink");
        File.Exists(Path.Combine(outside.Path, "planted.txt")).ShouldBeFalse();
        (await _folder.OpenReadAsync(P("escape/anything"), Ct)).Error.Code.ShouldBe("Local.Unreadable");
        (await _folder.CreateDirectoryAsync(P("escape/sub"), Ct)).IsFailure.ShouldBeTrue();
        Directory.Exists(Path.Combine(outside.Path, "sub")).ShouldBeFalse();
    }

    [Fact]
    public async Task ASymlinkItself_Should_NotBeReportedAsAFile_NorOpened()
    {
        _temp.WriteFile("real.txt", "real");
        CreateFileLink("link.txt", _temp.Combine("real.txt"));

        (await _folder.GetFileAsync(P("link.txt"), Ct)).Value.ShouldBeNull();
        (await _folder.OpenReadAsync(P("link.txt"), Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task OnWindows_ANameWindowsCantHold_Should_BeRefused_NotWrittenAsAStream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only rule");

        (await Place("report.txt:hidden", "x")).Error.Code.ShouldBe("Local.WriteFailed");
        (await Place("CON", "x")).Error.Code.ShouldBe("Local.WriteFailed");
        File.Exists(_temp.Combine("report.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Place_Should_ReplaceAReadOnlyFile()
    {
        _temp.WriteFile("locked.txt", "old");
        File.SetAttributes(_temp.Combine("locked.txt"), FileAttributes.ReadOnly);

        (await Place("locked.txt", "new")).IsSuccess.ShouldBeTrue();

        _temp.ReadText("locked.txt").ShouldBe("new");
    }

    [Fact]
    public async Task Place_Should_SetModeAndMtimeBeforeTheFileAppears_AndLeaveNothingStaged()
    {
        (await Place("run.sh", "#!/bin/sh", executable: true)).IsSuccess.ShouldBeTrue();

        File.GetLastWriteTimeUtc(_temp.Combine("run.sh")).ShouldBe(Mtime.UtcDateTime);
        _temp.IsExecutable("run.sh").ShouldBe(!OperatingSystem.IsWindows());
        Directory.EnumerateFiles(_temp.Combine(StateFileNames.StagingDirectory)).ShouldBeEmpty();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task ExecutableBit_Should_FollowReadPermissions_LikeChmodPlusX()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bits on Windows");
        _temp.WriteFile("private.sh", "x");
        File.SetUnixFileMode(_temp.Combine("private.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (await _folder.SetExecutableAsync(P("private.sh"), executable: true, Ct)).IsSuccess.ShouldBeTrue();

        File.GetUnixFileMode(_temp.Combine("private.sh")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task DeleteDirectoryIfEmpty_Should_TreatOsClutterAsEmpty_AndRemoveIt()
    {
        _temp.WriteFile("browsed/.DS_Store", "finder").WriteFile("browsed/Thumbs.db", "explorer").WriteFile("browsed/._x", "fork");
        _temp.WriteFile("kept/.DS_Store", "finder").WriteFile("kept/real.txt", "keep me");

        (await _folder.DeleteDirectoryIfEmptyAsync(P("browsed"), Ct)).Value.ShouldBeTrue();
        (await _folder.DeleteDirectoryIfEmptyAsync(P("kept"), Ct)).Value.ShouldBeFalse();

        Directory.Exists(_temp.Combine("browsed")).ShouldBeFalse();
        _temp.ReadText("kept/real.txt").ShouldBe("keep me");
        _temp.Exists("kept/.DS_Store").ShouldBeTrue();
    }

    [Fact]
    public async Task CleanStaging_Should_RemoveTheStagingDirectory_EvenWithAnOpenDownload()
    {
        IStagedFile staged = (await _folder.CreateStagedFileAsync(Ct)).Value;
        await staged.Content.WriteAsync(Encoding.UTF8.GetBytes("partial"), Ct);
        _temp.WriteFile($"{StateFileNames.StagingDirectory}/left-over-from-a-crash.part", "junk");

        (await _folder.CleanStagingAsync(Ct)).IsSuccess.ShouldBeTrue();

        Directory.Exists(_temp.Combine(StateFileNames.StagingDirectory)).ShouldBeFalse();
        await staged.DisposeAsync();
    }

    [Fact]
    public void IsCaseSensitive_Should_MatchWhatTheFilesystemDoes()
    {
        File.WriteAllText(_temp.Combine("probe"), "x");
        bool expected = !File.Exists(_temp.Combine("PROBE"));

        _folder.IsCaseSensitive.ShouldBe(expected);
        if (OperatingSystem.IsWindows())
        {
            _folder.IsCaseSensitive.ShouldBeFalse();
        }
    }

    [Fact]
    public void IsUnsafeRoot_Should_BeTrueForHomeAndTheFilesystemRoot_AndFalseForAnOrdinaryFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        new LocalFolder(new ShareRoot(home)).IsUnsafeRoot.ShouldBeTrue();
        new LocalFolder(new ShareRoot(Path.GetPathRoot(_temp.Path)!)).IsUnsafeRoot.ShouldBeTrue();
        _folder.IsUnsafeRoot.ShouldBeFalse();
    }

    [Fact]
    public async Task ListTopLevelNames_Should_ReturnNfcNames()
    {
        _temp.WriteFile("café.txt", "x");

        (await _folder.ListTopLevelNamesAsync(Ct)).Value.ShouldBe(["café.txt"]);
    }

    public void Dispose()
    {
        _temp.Dispose();
        foreach (TempShareFolder other in _others)
        {
            other.Dispose();
        }
    }

    private async Task<Result> Place(string path, string content, bool executable = false)
    {
        await using IStagedFile staged = (await _folder.CreateStagedFileAsync(Ct)).Value;
        await staged.Content.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
        return await _folder.PlaceStagedFileAsync(staged, P(path), Mtime, executable, Ct);
    }

    private void CreateDirectoryLink(string name, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(_temp.Combine(name), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }
    }

    private void CreateFileLink(string name, string target)
    {
        try
        {
            File.CreateSymbolicLink(_temp.Combine(name), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }
    }

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);
}
