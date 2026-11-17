using ConsoleAppFramework;
using MySync.Application.Shares.Clone;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync.Plan;
using MySync.Application.Sync.Run;
using MySync.Cli.Rendering;
using MySync.Domain.Shares;
using SharedKernel;
using Spectre.Console;

namespace MySync.Cli.Commands;

/// <summary>
/// <c>mysync sync [&lt;shareId&gt;]</c> (CLAUDE.md §9, §10): create a share, clone one, or two-way
/// sync. Thin: it picks the flow, asks the one question mysync ever asks, runs the handler and
/// renders the result. Confirmation never reaches the Application layer.
/// </summary>
internal sealed class SyncCommands(CommandRunner runner)
{
    /// <summary>Above either of these, creating a share asks first (or needs --yes without a terminal).</summary>
    internal const int LargeFileCount = 1000;

    internal const long LargeTotalBytes = 1024L * 1024 * 1024;

    private static readonly Error ConfirmationRequired = Error.Validation(
        "Cli.ConfirmationRequired",
        $"This folder is large (over {LargeFileCount} files or {LargeTotalBytes / 1024 / 1024 / 1024} GB) and there is no terminal to confirm on. Run again with --yes to create the share.");

    private static readonly Error DryRunWithShareId = Error.Validation(
        "Cli.DryRunWithShareId", "--dry-run can't be combined with a share ID: cloning only downloads. Run it in the cloned folder afterwards.");

    /// <summary>
    /// Sync this folder: create a share from it, clone a share into it (give the share ID), or two-way sync it if it is already a share.
    /// </summary>
    /// <param name="shareId">Clone this share into the current (empty) folder.</param>
    /// <param name="dryRun">-n, Show what would happen without changing anything.</param>
    /// <param name="noDelete">Never delete, here or in the share; deletions wait for a later run.</param>
    /// <param name="allowMassDelete">Allow deleting more than half of the files (more than 10).</param>
    /// <param name="yes">-y, Don't ask before creating a large share.</param>
    /// <param name="force">Create a share in your home folder or a drive root, or clone into a folder that isn't empty.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("sync")]
    public Task<int> Sync(
        [Argument] string? shareId = null,
        bool dryRun = false,
        bool noDelete = false,
        bool allowMassDelete = false,
        bool yes = false,
        bool force = false,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            if (shareId is not null)
            {
                return dryRun ? runner.Fail(DryRunWithShareId) : await CloneAsync(shareId, force, cancellationToken);
            }

            Result<NewSharePreview> preview = await runner.QueryAsync<PreviewNewShareQuery, NewSharePreview>(new PreviewNewShareQuery(), cancellationToken);
            if (preview.IsFailure)
            {
                return runner.Fail(preview.Error);
            }

            return preview.Value.ExistingShareId is null
                ? await CreateAsync(preview.Value, dryRun, yes, force, cancellationToken)
                : await RunSyncAsync(dryRun, noDelete, allowMassDelete, cancellationToken);
        }, cancellationToken);

    /// <summary>Show what 'mysync sync' would do now, without changing anything here or in the share.</summary>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("status")]
    public Task<int> Status(CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<SyncReport> planned = await runner.QueryAsync<GetSyncPlanQuery, SyncReport>(new GetSyncPlanQuery(), cancellationToken);
            if (planned.IsFailure)
            {
                return runner.Fail(planned.Error);
            }

            int exitCode = ExitCodeOf(planned.Value);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(planned.Value, "status", exitCode);
            }

            PlanView.Render(planned.Value, runner.Options.Verbose, status: true);
            return exitCode;
        }, cancellationToken);

    /// <summary>Exit code for a finished run: conflicts (3) win over skipped paths (9).</summary>
    internal static int ExitCodeOf(SyncReport report)
    {
        if (report.Conflicts.Length > 0)
        {
            return ExitCodes.ConflictsOpen;
        }

        return report.Skipped.Length > 0 ? ExitCodes.PathsSkipped : ExitCodes.Success;
    }

    private async Task<int> CreateAsync(NewSharePreview preview, bool dryRun, bool yes, bool force, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            if (runner.Options.Json)
            {
                return JsonOutput.Write(preview);
            }

            PlanView.Render(preview);
            return ExitCodes.Success;
        }

        // Refused before asking anything: no point confirming what will be refused anyway.
        if (preview.IsUnsafeRoot && !force)
        {
            return runner.Fail(ShareErrors.UnsafeRoot(preview.RootPath));
        }

        bool large = preview.FileCount > LargeFileCount || preview.TotalBytes > LargeTotalBytes;
        if (large && !yes)
        {
            if (!ConsoleOutput.IsInteractive || runner.Options.Json)
            {
                return runner.Fail(ConfirmationRequired);
            }

            bool confirmed = await ConsoleOutput.Errors.ConfirmAsync(
                $"Create a share from {preview.FileCount} files ({ConsoleOutput.Bytes(preview.TotalBytes)}) in {Markup.Escape(preview.RootPath)}?",
                defaultValue: false,
                cancellationToken);
            if (!confirmed)
            {
                ConsoleOutput.Info("Nothing was uploaded.");
                return ExitCodes.Cancelled;
            }
        }

        Result<CreateShareResponse> created = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
            runner.SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(force, runner.Options.Parallel, progress), cancellationToken));
        if (created.IsFailure)
        {
            return runner.Fail(created.Error);
        }

        int exitCode = created.Value.Skipped.Length > 0 ? ExitCodes.PathsSkipped : ExitCodes.Success;
        if (runner.Options.Json)
        {
            return JsonOutput.Write(created.Value, exitCode);
        }

        SyncReportView.Render(created.Value);
        return exitCode;
    }

    private async Task<int> CloneAsync(string shareId, bool force, CancellationToken cancellationToken)
    {
        Result<SyncReport> cloned = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
            runner.SendAsync<CloneShareCommand, SyncReport>(new CloneShareCommand(shareId, force, runner.Options.Parallel, progress), cancellationToken));

        return Finish(cloned, "clone");
    }

    private async Task<int> RunSyncAsync(bool dryRun, bool noDelete, bool allowMassDelete, CancellationToken cancellationToken)
    {
        Result<SyncReport> synced = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
            runner.SendAsync<RunSyncCommand, SyncReport>(new RunSyncCommand(dryRun, noDelete, allowMassDelete, runner.Options.Parallel, progress), cancellationToken));

        return Finish(synced, "sync");
    }

    private int Finish(Result<SyncReport> result, string command)
    {
        if (result.IsFailure)
        {
            return runner.Fail(result.Error);
        }

        SyncReport report = result.Value;
        int exitCode = ExitCodeOf(report);
        if (runner.Options.Json)
        {
            return JsonOutput.Write(report, command, exitCode);
        }

        if (report.DryRun)
        {
            PlanView.Render(report, runner.Options.Verbose);
        }
        else
        {
            SyncReportView.Render(report, cloned: command == "clone");
        }

        return exitCode;
    }
}
