using System.Diagnostics;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.State;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.State;

/// <summary><c>.mysynctool.lock</c> (CLAUDE.md §7): one mysync per folder.</summary>
public sealed class FolderLockTests : IDisposable
{
    private readonly TempShareFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASecondHolder_Should_GetFolderLocked_UntilTheFirstReleases()
    {
        FolderLock first = new(new ShareRoot(_temp.Path));
        FolderLock second = new(new ShareRoot(_temp.Path));

        Result<IAsyncDisposable> held = await first.AcquireAsync(Ct);
        held.IsSuccess.ShouldBeTrue();
        (await second.AcquireAsync(Ct)).Error.ShouldBe(StateErrors.FolderLocked);

        await held.Value.DisposeAsync();
        Result<IAsyncDisposable> again = await second.AcquireAsync(Ct);
        again.IsSuccess.ShouldBeTrue();
        await again.Value.DisposeAsync();
    }

    [Fact]
    public async Task TheLockFile_Should_StayInPlace_WhenReleased()
    {
        FolderLock folderLock = new(new ShareRoot(_temp.Path));

        await (await folderLock.AcquireAsync(Ct)).Value.DisposeAsync();

        _temp.Exists(StateFileNames.Lock).ShouldBeTrue();
    }

    [Fact]
    public async Task AnotherProcessHoldingTheLock_Should_BlockThisOne()
    {
        // Unix locks (flock) are per open file, but this proves it across processes as well.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses the flock command");
        Assert.SkipUnless(File.Exists("/usr/bin/flock") || File.Exists("/bin/flock"), "flock(1) is not installed (macOS)");

        using Process holder = Process.Start(new ProcessStartInfo("flock", ["-x", _temp.Combine(StateFileNames.Lock), "sleep", "30"]))!;
        try
        {
            FolderLock folderLock = new(new ShareRoot(_temp.Path));
            var result = Result.Success<IAsyncDisposable>(null!);
            for (int attempt = 0; attempt < 50 && result.IsSuccess; attempt++)
            {
                await Task.Delay(100, Ct);
                result = await folderLock.AcquireAsync(Ct);
                if (result.IsSuccess)
                {
                    await result.Value.DisposeAsync(); // flock hasn't started yet
                }
            }

            result.Error.ShouldBe(StateErrors.FolderLocked);
        }
        finally
        {
            holder.Kill();
        }
    }

    public void Dispose() => _temp.Dispose();
}
