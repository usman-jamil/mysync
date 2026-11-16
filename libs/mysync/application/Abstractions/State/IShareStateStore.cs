using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Abstractions.State;

/// <summary><c>.mysynctool</c> (CLAUDE.md §7), written atomically.</summary>
public interface IShareStateStore
{
    /// <summary>
    /// The folder's state, or a <c>null</c> value if there is no <c>.mysynctool</c>. Unreadable or
    /// damaged is <c>State.Corrupt</c>, never <c>null</c>: a damaged file must not turn into a new share.
    /// </summary>
    Task<Result<ShareState?>> LoadAsync(CancellationToken cancellationToken);

    Task<Result> SaveAsync(ShareState state, CancellationToken cancellationToken);
}

/// <summary>
/// The per-folder process lock (<c>.mysynctool.lock</c>), held for a whole run. A second mysync in
/// the same folder gets <c>State.FolderLocked</c>.
/// </summary>
public interface IFolderLock
{
    /// <summary>Disposing the returned handle releases the lock.</summary>
    Task<Result<IAsyncDisposable>> AcquireAsync(CancellationToken cancellationToken);
}
