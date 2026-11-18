using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using MySync.Infrastructure.Updates;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Updates;

/// <summary>The real installer on temp folders: what it unpacks, what it runs, and how it swaps.</summary>
public sealed class ExecutableInstallerTests : IDisposable
{
    private readonly TempShareFolder _temp = new();
    private readonly ExecutableInstaller _installer = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Locate_Should_DescribeThisProcess()
    {
        InstalledExecutable installed = _installer.Locate();

        installed.RuntimeIdentifier.ShouldNotBeNullOrWhiteSpace();
        installed.Path.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ATarGz_Should_GiveUpOnlyTheExecutable_MadeExecutable()
    {
        await using IUpdateWorkspace workspace = await WorkspaceAsync();
        await WriteTarGzAsync(workspace, "mysync-1.0.0-linux-x64.tar.gz", ("./mysync", "binary"), ("./README", "not wanted"));

        string extracted = Ok(await workspace.ExtractExecutableAsync("mysync-1.0.0-linux-x64.tar.gz", "mysync", Ct));

        (await File.ReadAllTextAsync(extracted, Ct)).ShouldBe("binary");
        Directory.GetFiles(Path.GetDirectoryName(extracted)!).Length.ShouldBe(1);
        if (!OperatingSystem.IsWindows())
        {
            (File.GetUnixFileMode(extracted) & UnixFileMode.UserExecute).ShouldNotBe(UnixFileMode.None);
        }
    }

    [Fact]
    public async Task AnArchiveEntryThatClimbsOut_Should_NeverBeWrittenOutside()
    {
        await using IUpdateWorkspace workspace = await WorkspaceAsync();
        await WriteTarGzAsync(workspace, "evil.tar.gz", ("../../escaped", "planted"), ("./mysync", "binary"));

        Ok(await workspace.ExtractExecutableAsync("evil.tar.gz", "mysync", Ct));

        Directory.EnumerateFiles(_temp.Path, "escaped", SearchOption.AllDirectories).ShouldBeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(_temp.Path)!, "escaped")).ShouldBeFalse();
    }

    [Fact]
    public async Task AZip_Should_GiveUpTheWindowsExecutable()
    {
        await using IUpdateWorkspace workspace = await WorkspaceAsync();
        await using (Stream file = workspace.CreateFile("mysync-1.0.0-win-x64.zip"))
        {
            using ZipArchive zip = new(file, ZipArchiveMode.Create);
            await using Stream entry = await zip.CreateEntry("mysync.exe").OpenAsync(Ct);
            await entry.WriteAsync(Encoding.UTF8.GetBytes("exe"), Ct);
        }

        string extracted = Ok(await workspace.ExtractExecutableAsync("mysync-1.0.0-win-x64.zip", "mysync.exe", Ct));

        (await File.ReadAllTextAsync(extracted, Ct)).ShouldBe("exe");
    }

    [Fact]
    public async Task AnArchiveWithoutTheExecutable_OrNotAnArchiveAtAll_Should_FailCleanly()
    {
        await using IUpdateWorkspace workspace = await WorkspaceAsync();
        await WriteTarGzAsync(workspace, "empty.tar.gz", ("./something-else", "x"));
        await using (Stream file = workspace.CreateFile("junk.tar.gz"))
        {
            await file.WriteAsync(Encoding.UTF8.GetBytes("this is not gzip"), Ct);
        }

        (await workspace.ExtractExecutableAsync("empty.tar.gz", "mysync", Ct)).Error.Code.ShouldBe("Update.NewBinaryFailed");
        (await workspace.ExtractExecutableAsync("junk.tar.gz", "mysync", Ct)).Error.Code.ShouldBe("Update.NewBinaryFailed");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Run_Should_CaptureTheOutputAndExitCode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses a shell script as the binary");
        await using IUpdateWorkspace workspace = await WorkspaceAsync();
        string script = _temp.Combine("fake-mysync");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho 9.9.9\nexit 3\n", Ct);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        ProcessOutcome outcome = Ok(await workspace.RunAsync(script, ["--version"], Ct));

        outcome.ExitCode.ShouldBe(3);
        outcome.Output.Trim().ShouldBe("9.9.9");
        (await workspace.RunAsync(_temp.Combine("does-not-exist"), [], Ct)).Error.Code.ShouldBe("Update.NewBinaryFailed");
    }

    [Fact]
    public async Task Install_Should_PutTheNewFileInPlace_AndDisposeShouldRemoveTheWorkspace()
    {
        string target = _temp.Combine("mysync");
        await File.WriteAllTextAsync(target, "old", Ct);
        InstalledExecutable installed = Installed(target);
        IUpdateWorkspace workspace = Ok(await _installer.CreateWorkspaceAsync(installed, Ct));
        string fresh = _temp.Combine("fresh");
        await File.WriteAllTextAsync(fresh, "new", Ct);

        Ok(await workspace.InstallAsync(fresh, installed, Ct));
        await workspace.DisposeAsync();

        (await File.ReadAllTextAsync(target, Ct)).ShouldBe("new");
        Directory.EnumerateDirectories(_temp.Path, ".mysync-update-*").ShouldBeEmpty();
        if (OperatingSystem.IsWindows())
        {
            // The old .exe is renamed aside, then removed on a later start.
            Directory.EnumerateFiles(_temp.Path, "mysync.old-*").Count().ShouldBe(1);
            UpdateLeftovers.RemoveNextTo(target);
            Directory.EnumerateFiles(_temp.Path, "mysync.old-*").ShouldBeEmpty();
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AFolderThisUserCantWrite_Should_BeReportedWithAdvice()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Unix permissions, as a normal user");
        string folder = _temp.Combine("locked");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "mysync");
        File.WriteAllText(target, "old");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Error error = _installer.CheckReplaceable(Installed(target)).Error;

        error.Code.ShouldBe("Update.NotWritable");
        error.Description.ShouldContain("sudo mysync update");
        _installer.CheckReplaceable(Installed(_temp.Combine("mysync"))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Leftovers_Should_RemoveOldCopiesAndStaleWorkspaces_ButNotAFreshOne()
    {
        string executable = _temp.Combine("mysync");
        File.WriteAllText(executable, "current");
        File.WriteAllText(executable + ".old-abc", "previous");
        string stale = _temp.Combine(".mysync-update-stale");
        string fresh = _temp.Combine(".mysync-update-fresh");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        UpdateLeftovers.RemoveNextTo(executable);

        File.Exists(executable).ShouldBeTrue();
        File.Exists(executable + ".old-abc").ShouldBeFalse();
        Directory.Exists(stale).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
    }

    public void Dispose() => _temp.Dispose();

    private static InstalledExecutable Installed(string path) =>
        new(path, "linux-x64", ReleaseVersion.TryParse("1.0.0", out ReleaseVersion? version) ? version : null!, null);

    private async Task<IUpdateWorkspace> WorkspaceAsync()
    {
        string target = _temp.Combine("mysync");
        await File.WriteAllTextAsync(target, "old", Ct);
        return Ok(await _installer.CreateWorkspaceAsync(Installed(target), Ct));
    }

    private static async Task WriteTarGzAsync(IUpdateWorkspace workspace, string name, params (string Path, string Content)[] entries)
    {
        await using Stream file = workspace.CreateFile(name);
        await using GZipStream gzip = new(file, CompressionLevel.Fastest);
        await using TarWriter tar = new(gzip, TarEntryFormat.Pax);
        foreach ((string path, string content) in entries)
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
            await tar.WriteEntryAsync(entry, Ct);
        }
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    private static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
}
