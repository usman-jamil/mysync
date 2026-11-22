using ConsoleAppFramework;
using MySync.Application.Sync.Push;
using MySync.Cli.Rendering;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync push</c> (CLAUDE.md §10b): a one-way, add-only upload for collecting data from servers.</summary>
internal sealed class PushCommands(CommandRunner runner)
{
    private static readonly Error PushNeedsShare = Error.Validation(
        "Cli.PushNeedsShare", "Name the share to push into: mysync push <name|shareId> [<path>...]");

    /// <summary>
    /// Upload files into a new folder (host/UTC time) of an existing share. Never downloads,
    /// deletes or conflicts, and keeps no state on this machine.
    /// </summary>
    /// <param name="share">The share, by name or ID.</param>
    /// <param name="paths">Files and folders to push (default: this folder's contents).</param>
    /// <param name="host">The host folder's name (default: this machine's name).</param>
    /// <param name="create">Create the share (by name) if it doesn't exist.</param>
    /// <param name="noInfo">Don't add _mysync-push.json (where the data came from) to the folder.</param>
    /// <param name="dryRun">-n, Read the files and show what would be pushed, without uploading anything.</param>
    /// <param name="force">Allow the home folder or a drive root as a source.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("push")]
    public Task<int> Push(
        [Argument] string? share = null,
        string? host = null,
        bool create = false,
        bool noInfo = false,
        bool dryRun = false,
        bool force = false,
        CancellationToken cancellationToken = default,
        [Argument] params string[] paths) =>
        runner.RunAsync(async () =>
        {
            if (share is null)
            {
                return runner.Fail(PushNeedsShare);
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

            PushCommand command = new(reference.Value, paths, host, create, !noInfo, dryRun, force, runner.Options.Parallel);
            Result<PushReport> pushed = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
                runner.SendAsync<PushCommand, PushReport>(command with { Progress = progress }, cancellationToken));
            if (pushed.IsFailure)
            {
                int failed = runner.Fail(pushed.Error);
                if (!runner.Options.Json && pushed.Error.Code is "Shares.NotFound" or "Shares.NameNotFound")
                {
                    string name = reference.Value.Name?.Value ?? "<name>";
                    ConsoleOutput.Info($"Create it with: mysync push {name} --create, or mysync sync --name {name} in a folder");
                }

                return failed;
            }

            int exitCode = ExitCodeOf(pushed.Value);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(pushed.Value, exitCode);
            }

            PushReportView.Render(pushed.Value);
            return exitCode;
        }, cancellationToken);

    /// <summary>A push never has conflicts or trips the mass-delete guard: 0, or 9 if paths were skipped.</summary>
    internal static int ExitCodeOf(PushReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Skipped.IsEmpty ? ExitCodes.Success : ExitCodes.PathsSkipped;
    }
}
