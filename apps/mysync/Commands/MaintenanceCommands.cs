using ConsoleAppFramework;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.Maintenance.Update;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync gc</c> and <c>mysync update</c> (CLAUDE.md §10). Neither runs on its own.</summary>
internal sealed class MaintenanceCommands(CommandRunner runner)
{
    /// <summary>
    /// Update mysync to the latest release (verified and tested before it replaces this one).
    /// </summary>
    /// <param name="check">Only say whether an update is available; download nothing.</param>
    /// <param name="version">Install this release instead, even an older one, so a team can stay on one version.</param>
    /// <param name="prerelease">Let the latest be a prerelease.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("update")]
    public Task<int> Update(bool check = false, string? version = null, bool prerelease = false, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result<UpdateReport> updated = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
                runner.SendAsync<UpdateCommand, UpdateReport>(new UpdateCommand(check, version, prerelease, progress), cancellationToken));
            if (updated.IsFailure)
            {
                return runner.Fail(updated.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(updated.Value, ExitCodes.Success);
            }

            UpdateView.Render(updated.Value);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>
    /// Delete stored files no recent revision uses, and old history. Run it when no one else is syncing.
    /// </summary>
    /// <param name="dryRun">-n, List what would be deleted without deleting it.</param>
    /// <param name="keepRevisions">Keep everything the newest N revisions use.</param>
    /// <param name="graceHours">Never delete anything uploaded in the last N hours.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("gc")]
    public Task<int> Gc(
        bool dryRun = false,
        int keepRevisions = CollectGarbageCommand.DefaultKeepRevisions,
        int graceHours = CollectGarbageCommand.DefaultGraceHours,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<GarbageReport> collected = await runner.SendAsync<CollectGarbageCommand, GarbageReport>(
                new CollectGarbageCommand(keepRevisions, graceHours, dryRun), cancellationToken);
            if (collected.IsFailure)
            {
                return runner.Fail(collected.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(collected.Value, ExitCodes.Success);
            }

            GarbageView.Render(collected.Value, runner.Options.Verbose);
            return ExitCodes.Success;
        }, cancellationToken);
}
