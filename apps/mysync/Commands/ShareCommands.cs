using ConsoleAppFramework;
using MySync.Application.Shares.GetInfo;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync info</c> (CLAUDE.md §10).</summary>
internal sealed class ShareCommands(CommandRunner runner)
{
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
