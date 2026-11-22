using ConsoleAppFramework;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.Compression;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.List;
using MySync.Application.Shares.Rename;
using MySync.Domain.Shares;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync info</c>, <c>delete</c>, <c>shares</c> and <c>rename</c> (CLAUDE.md §10).</summary>
internal sealed class ShareCommands(CommandRunner runner)
{
    private static readonly Error CompressionNeedsOnOrOff = Error.Validation(
        "Cli.CompressionNeedsOnOrOff", "Use 'mysync compression on' or 'mysync compression off' (or no argument to show the setting).");

    private static readonly Error RenameNeedsName = Error.Validation(
        "Cli.RenameNeedsName", "Give the new name: mysync rename <name> (3-40 letters a-z, digits and '-').");

    private static readonly Error DeleteNeedsYes = Error.Validation(
        "Cli.DeleteNeedsYes",
        "Deleting a share removes every file stored in it, for every machine that syncs it, and can't be undone. Run again with --yes to delete it.");

    /// <summary>
    /// Delete a share for good: every stored file and its history, for every machine. Local files are kept.
    /// </summary>
    /// <param name="shareId">The share to delete, by name or ID (default: this folder's).</param>
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

            Result<Domain.Shares.ShareReference?> reference = CommandRunner.ParseShare(shareId);
            if (reference.IsFailure)
            {
                return runner.Fail(reference.Error);
            }

            Result<DeleteShareResponse> deleted = await runner.SendAsync<DeleteShareCommand, DeleteShareResponse>(
                new DeleteShareCommand(reference.Value, runner.Options.Parallel), cancellationToken);
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

    /// <summary>Show a share: its names, ID, storage, revision here and in the share, files, open conflicts.</summary>
    /// <param name="share">A share's name or ID (default: this folder's).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("info")]
    public Task<int> Info([Argument] string? share = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<ShareReference?> reference = CommandRunner.ParseShare(share);
            if (reference.IsFailure)
            {
                return runner.Fail(reference.Error);
            }

            Result<ShareInfoResponse> info = await runner.QueryAsync<GetShareInfoQuery, ShareInfoResponse>(new GetShareInfoQuery(reference.Value), cancellationToken);
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

    /// <summary>List every named share: name and share ID.</summary>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("shares")]
    public Task<int> Shares(CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<ShareListResponse> listed = await runner.QueryAsync<ListSharesQuery, ShareListResponse>(new ListSharesQuery(), cancellationToken);
            if (listed.IsFailure)
            {
                return runner.Fail(listed.Error);
            }

            foreach (string warning in listed.Value.Warnings)
            {
                ConsoleOutput.Warn(warning);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(listed.Value, ExitCodes.Success);
            }

            ShareInfoView.Render(listed.Value);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Give this folder's share a new name (or a first name, for a share from an older mysync).</summary>
    /// <param name="name">The new name: 3-40 letters a-z, digits and '-'.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("rename")]
    public Task<int> Rename([Argument] string? name = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            if (name is null)
            {
                return runner.Fail(RenameNeedsName);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<ShareName> newName = ShareName.Create(name);
            if (newName.IsFailure)
            {
                return runner.Fail(newName.Error);
            }

            Result<RenameShareResponse> renamed = await runner.SendAsync<RenameShareCommand, RenameShareResponse>(new RenameShareCommand(newName.Value), cancellationToken);
            if (renamed.IsFailure)
            {
                return runner.Fail(renamed.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(renamed.Value, ExitCodes.Success);
            }

            ShareInfoView.Render(renamed.Value);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Show the share's compression setting, or turn it on or off for files uploaded from now on.</summary>
    /// <param name="state">on or off (leave out to show the setting).</param>
    /// <param name="share">A share's name or ID (default: this folder's).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("compression")]
    public Task<int> Compression([Argument] string? state = null, string? share = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            bool? on = state?.ToUpperInvariant() switch
            {
                "ON" => true,
                "OFF" => false,
                _ => null,
            };
            if (state is not null && on is null)
            {
                return runner.Fail(CompressionNeedsOnOrOff);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<ShareReference?> reference = CommandRunner.ParseShare(share);
            if (reference.IsFailure)
            {
                return runner.Fail(reference.Error);
            }

            Result<CompressionResponse> result = on is { } turnOn
                ? await runner.SendAsync<SetCompressionCommand, CompressionResponse>(new SetCompressionCommand(turnOn, reference.Value), cancellationToken)
                : await runner.QueryAsync<GetCompressionQuery, CompressionResponse>(new GetCompressionQuery(reference.Value), cancellationToken);
            if (result.IsFailure)
            {
                return runner.Fail(result.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(result.Value, ExitCodes.Success);
            }

            ShareInfoView.Render(result.Value, changing: on is not null);
            return ExitCodes.Success;
        }, cancellationToken);
}
