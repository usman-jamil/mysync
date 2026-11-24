using ConsoleAppFramework;
using MySync.Application.Shares.CloneInto;
using MySync.Cli.Rendering;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary>
/// <c>mysync clone &lt;share&gt; [&lt;folder&gt;]</c> (CLAUDE.md §9, "<c>mysync clone</c>"): like
/// <c>git clone</c>, the folder is created for the share. <c>mysync sync &lt;share&gt;</c> in an
/// empty folder is unchanged.
/// </summary>
internal sealed class CloneCommands(CommandRunner runner)
{
    private static readonly Error CloneNeedsShare = Error.Validation(
        "Cli.CloneNeedsShare", "Name the share to clone: mysync clone <name|shareId> [<folder>]");

    /// <summary>
    /// Clone a share into a new folder named after it (or the folder given), created for it. If
    /// the clone fails or is cancelled, what it created is removed again.
    /// </summary>
    /// <param name="share">The share, by name or ID.</param>
    /// <param name="folder">Where to clone it (default: a new folder named after the share, here). Created if missing; must be empty if it exists.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("clone")]
    public Task<int> Clone([Argument] string? share = null, [Argument] string? folder = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            if (share is null)
            {
                return runner.Fail(CloneNeedsShare);
            }

            Result<ShareReference> reference = ShareReference.Parse(share);
            if (reference.IsFailure)
            {
                return runner.Fail(reference.Error);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            CloneCleanUpReport? cleanUp = null;
            Result<CloneIntoFolderResponse> cloned;
            try
            {
                cloned = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
                    runner.SendAsync<CloneIntoFolderCommand, CloneIntoFolderResponse>(
                        new CloneIntoFolderCommand(reference.Value, folder, runner.Options.Parallel, progress, report => cleanUp = report),
                        cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CloneView.RenderCleanUp(cleanUp);
                throw;
            }

            if (cloned.IsFailure)
            {
                // The original error first, then what the clean-up left behind.
                int failed = ConsoleOutput.Fail(cloned.Error);
                CloneView.RenderCleanUp(cleanUp);
                return runner.Options.Json ? JsonOutput.Write(cloned.Error, failed, cleanUp?.LeftBehind) : failed;
            }

            int exitCode = SyncCommands.ExitCodeOf(cloned.Value.Report);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(cloned.Value, exitCode);
            }

            CloneView.Render(cloned.Value, Environment.CurrentDirectory);
            return exitCode;
        }, cancellationToken);
}
