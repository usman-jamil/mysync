using ConsoleAppFramework;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync info</c> and <c>mysync delete</c> (CLAUDE.md §10).</summary>
internal sealed class ShareCommands(CommandRunner runner)
{
    private static readonly Error DeleteNeedsYes = Error.Validation(
        "Cli.DeleteNeedsYes",
        "Deleting a share removes every file stored in it, for every machine that syncs it, and can't be undone. Run again with --yes to delete it.");

    /// <summary>
    /// Delete a share for good: every stored file and its history, for every machine. Local files are kept.
    /// </summary>
    /// <param name="shareId">The share to delete (default: this folder's).</param>
    /// <param name="yes">Required: confirms the deletion. mysync never asks.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("delete")]
    public Task<int> Delete([Argument] string? shareId = null, bool yes = false, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            if (!yes)
            {
                return runner.Fail(DeleteNeedsYes);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<DeleteShareResponse> deleted = await runner.SendAsync<DeleteShareCommand, DeleteShareResponse>(
                new DeleteShareCommand(shareId, runner.Options.Parallel), cancellationToken);
            if (deleted.IsFailure)
            {
                return runner.Fail(deleted.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(deleted.Value, ExitCodes.Success);
            }

            ShareInfoView.Render(deleted.Value);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Show this folder's share: ID, storage, revision here and in the share, files, open conflicts.</summary>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("info")]
    public Task<int> Info(CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<ShareInfoResponse> info = await runner.QueryAsync<GetShareInfoQuery, ShareInfoResponse>(new GetShareInfoQuery(), cancellationToken);
            if (info.IsFailure)
            {
                return runner.Fail(info.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(info.Value, ExitCodes.Success);
            }

            ShareInfoView.Render(info.Value);
            return ExitCodes.Success;
        }, cancellationToken);
}
