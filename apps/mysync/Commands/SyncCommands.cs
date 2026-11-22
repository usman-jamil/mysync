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

    private const string CompressIsASetting = "--compress is ignored: compression is a setting of the share. Turn it on with: mysync compression on";

    private static readonly Error NameWithShareReference = Error.Validation(
        "Cli.NameWithShareReference",
        "--name names a new share; with a share name or ID, 'mysync sync' clones that share instead. Leave one of them out.");

    /// <summary>
    /// Sync this folder: create a share from it, clone a share into it (give the share ID), or two-way sync it if it is already a share.
    /// </summary>
    /// <param name="shareId">Clone this share, by name or ID, into the current (empty) folder.</param>
    /// <param name="dryRun">-n, Show what would happen without changing anything.</param>
    /// <param name="noDelete">Never delete, here or in the share; deletions wait for a later run.</param>
    /// <param name="allowMassDelete">Allow deleting more than half of the files (more than 10).</param>
    /// <param name="yes">-y, Don't ask before creating a large share.</param>
    /// <param name="force">Create a share in your home folder or a drive root, or clone into a folder that isn't empty.</param>
    /// <param name="name">The new share's name (required when creating one): 3-40 letters a-z, digits and '-'.</param>
    /// <param name="compress">Store the new share's files gzip-compressed (a setting of the share; see 'mysync compression').</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("sync")]
    public Task<int> Sync(
        [Argument] string? shareId = null,
        bool dryRun = false,
        bool noDelete = false,
        bool allowMassDelete = false,
        bool yes = false,
        bool force = false,
        string? name = null,
        bool compress = false,
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
                if (name is not null)
                {
                    return runner.Fail(NameWithShareReference);
                }

                if (compress)
                {
                    ConsoleOutput.Warn(CompressIsASetting);
                }

                return dryRun ? runner.Fail(DryRunWithShareId) : await CloneAsync(shareId, force, cancellationToken);
            }

            ShareName? newName = null;
            if (name is not null)
            {
                Result<ShareName> parsedName = ShareName.Create(name);
                if (parsedName.IsFailure)
                {
                    return runner.Fail(parsedName.Error);
                }

                newName = parsedName.Value;
            }

            // Without a name a new share can't be made, so don't walk the folder to find that out.
            bool canCreate = newName is not null || dryRun;
            Result<NewSharePreview> preview = await runner.QueryAsync<PreviewNewShareQuery, NewSharePreview>(new PreviewNewShareQuery(CountFiles: canCreate), cancellationToken);
            if (preview.IsFailure)
            {
                return runner.Fail(preview.Error);
            }

            if (preview.Value.ExistingShareId is not null)
            {
                if (newName is not null)
                {
                    ConsoleOutput.Warn($"--name is ignored: this folder is already a share. To rename it: mysync rename {newName}");
                }

                if (compress)
                {
                    ConsoleOutput.Warn(CompressIsASetting);
                }

                return await RunSyncAsync(dryRun, noDelete, allowMassDelete, cancellationToken);
            }

            if (!canCreate)
            {
                string folderName = Path.GetFileName(preview.Value.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return runner.Fail(ShareErrors.NameRequired(ShareName.SuggestFrom(folderName)));
            }

            return await CreateAsync(preview.Value, newName, dryRun, yes, force, compress, cancellationToken);
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

    private async Task<int> CreateAsync(NewSharePreview preview, ShareName? newName, bool dryRun, bool yes, bool force, bool compress, CancellationToken cancellationToken)
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
            runner.SendAsync<CreateShareCommand, CreateShareResponse>(new CreateShareCommand(newName, force, runner.Options.Parallel, progress, compress), cancellationToken));
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
        Result<ShareReference?> reference = CommandRunner.ParseShare(shareId);
        if (reference.IsFailure)
        {
            return runner.Fail(reference.Error);
        }

        Result<SyncReport> cloned = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
            runner.SendAsync<CloneShareCommand, SyncReport>(new CloneShareCommand(reference.Value!, force, runner.Options.Parallel, progress), cancellationToken));

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
