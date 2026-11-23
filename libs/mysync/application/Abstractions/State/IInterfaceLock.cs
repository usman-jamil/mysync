using SharedKernel;

namespace MySync.Application.Abstractions.State;

/// <summary>
/// <c>mysync tui</c>'s one-per-folder lock: <c>.mysynctool.ui</c>, held open with this process's ID
/// inside while the interface runs (CLAUDE.md §10d). Separate from the sync lock, so a CLI
/// <c>mysync sync</c> in the same folder still works; a file left by a crash is taken over.
/// </summary>
public interface IInterfaceLock
{
    /// <returns>The held lock (dispose to release), or <c>Tui.AlreadyRunning</c> with the other process's ID when it can be read.</returns>
    Task<Result<IAsyncDisposable>> AcquireAsync(CancellationToken cancellationToken);
}
