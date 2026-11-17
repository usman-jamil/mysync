using ConsoleAppFramework;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync gc</c> (CLAUDE.md §10). Never automatic.</summary>
internal sealed class MaintenanceCommands(CommandRunner runner)
{
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
