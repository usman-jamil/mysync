using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Application.Abstractions.Updates;

/// <summary>
/// The running mysync binary as a file on this machine: where it is, whether it can be replaced,
/// and a workspace next to it to stage a new one. The only part of <c>mysync update</c> that differs
/// between operating systems (how a running executable is swapped) lives behind this.
/// </summary>
public interface IExecutableInstaller
{
    /// <summary>What is running. Always succeeds for a mysync process; see <see cref="InstalledExecutable.CannotReplaceBecause"/>.</summary>
    InstalledExecutable Locate();

    /// <summary>Checked before anything is downloaded: <c>Update.NotWritable</c> (with what to do about it).</summary>
    Result CheckReplaceable(InstalledExecutable installed);

    /// <summary>A fresh folder on the same volume as the executable, so the final move is a rename.</summary>
    Task<Result<IUpdateWorkspace>> CreateWorkspaceAsync(InstalledExecutable installed, CancellationToken cancellationToken);
}

/// <param name="RuntimeIdentifier">The platform this binary was built for, e.g. <c>osx-arm64</c>: the archive to fetch.</param>
/// <param name="CannotReplaceBecause">Why this process can't be replaced (e.g. it runs as <c>dotnet mysync.dll</c>); <c>null</c> if it can.</param>
public sealed record InstalledExecutable(string Path, string RuntimeIdentifier, ReleaseVersion Version, string? CannotReplaceBecause);

/// <summary>A staging folder for one update. Disposing it deletes it, whatever happened.</summary>
public interface IUpdateWorkspace : IAsyncDisposable
{
    /// <summary>A new file in the workspace to download into.</summary>
    Stream CreateFile(string name);

    /// <summary>Extracts <paramref name="executableName"/> from the archive (and nothing else) and makes it executable. Returns its path.</summary>
    Task<Result<string>> ExtractExecutableAsync(string archiveName, string executableName, CancellationToken cancellationToken);

    /// <summary>Runs a binary from the workspace and captures its output, to test it before it is installed.</summary>
    Task<Result<ProcessOutcome>> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Puts <paramref name="executable"/> in place of the installed binary. Unix: rename over it (the
    /// running process keeps the old file). Windows: rename the running file aside, move the new one in,
    /// and remove the old one on a later start. On failure the installed binary is left as it was.
    /// </summary>
    Task<Result> InstallAsync(string executable, InstalledExecutable target, CancellationToken cancellationToken);
}

public sealed record ProcessOutcome(int ExitCode, string Output);
