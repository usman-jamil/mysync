using MySync.Application.Shares.Clone;
using MySync.Application.Sync.Run;
using SharedKernel;

namespace MySync.Application.Abstractions.Composition;

/// <summary>
/// Runs the existing clone (<see cref="CloneShareCommand"/>) in a folder other than the share root
/// this process was composed for: every folder-bound service (folder, scanner, state file, lock,
/// ignore rules, conflict log) is composed again for <c>root</c>. The process's current directory
/// is never changed. Implemented by the composition root (the CLI).
/// </summary>
public interface ICloneRunner
{
    Task<Result<SyncReport>> CloneAsync(string root, CloneShareCommand command, CancellationToken cancellationToken);
}
