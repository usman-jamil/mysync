using MySync.Application.Abstractions.State;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Infrastructure.State;

/// <summary>
/// <c>.mysynctool.lock</c> (CLAUDE.md §7): held open with <see cref="FileShare.None"/> for the
/// whole run. On Windows that is a sharing lock; on Unix .NET takes an exclusive <c>flock</c>.
/// Either way the operating system releases it if the process dies, so there is no stale lock to
/// clean up.
/// </summary>
/// <remarks>
/// The file is left in place when released. Deleting it on release would let a waiting process
/// lock the old, unlinked file while a third locks a new one at the same path, and both would run.
/// </remarks>
internal sealed class FolderLock(ShareRoot root) : IFolderLock
{
    public Task<Result<IAsyncDisposable>> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = root.Combine(StateFileNames.Lock);

        try
        {
#pragma warning disable CA2000 // Ownership passes to the caller, who disposes it to release the lock.
            FileStream handle = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
#pragma warning restore CA2000
            return Task.FromResult(Result.Success<IAsyncDisposable>(handle));
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure<IAsyncDisposable>(LocalFileErrors.WriteFailed(StateFileNames.Lock, exception.Message)));
        }
        catch (IOException)
        {
            return Task.FromResult(Result.Failure<IAsyncDisposable>(StateErrors.FolderLocked));
        }
    }
}
