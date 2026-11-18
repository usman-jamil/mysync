using ConsoleAppFramework;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Diagnostics.SelfCheck;
using MySync.Application.Diagnostics.SiteAssets;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

internal sealed class DiagnosticsCommands(CommandRunner runner)
{
    /// <summary>
    /// Verify this binary's runtime can do what mysync relies on (used by release smoke tests).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Command("self-check")]
    public Task<int> SelfCheck(CancellationToken cancellationToken) => runner.RunAsync(async () =>
    {
        Result<SelfCheckResponse> result = await runner.QueryAsync<RunSelfCheckQuery, SelfCheckResponse>(new RunSelfCheckQuery(), cancellationToken);

        if (result.IsFailure)
        {
            return ConsoleOutput.Fail(result.Error);
        }

        foreach (SelfCheckResult check in result.Value.Checks)
        {
            if (check.Passed)
            {
                ConsoleOutput.Success($"ok    {check.Name}: {check.Detail}");
            }
            else
            {
                ConsoleOutput.Warn($"FAIL  {check.Name}: {check.Detail}");
            }
        }

        return result.Value.Passed ? ExitCodes.Success : ExitCodes.Unexpected;
    }, cancellationToken);

    /// <summary>
    /// List the publication page assets embedded in this binary, with their SHA-256 (release smoke
    /// test). Exits 1 if they are the Debug placeholder.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Hidden]
    [Command("debug site-assets")]
    public Task<int> DebugSiteAssets(CancellationToken cancellationToken) => runner.RunAsync(async () =>
    {
        Result<SiteAssetsResponse> result = await runner.QueryAsync<ListSiteAssetsQuery, SiteAssetsResponse>(new ListSiteAssetsQuery(), cancellationToken);

        if (result.IsFailure)
        {
            return ConsoleOutput.Fail(result.Error);
        }

        foreach (SiteAssetInfo asset in result.Value.Assets)
        {
            ConsoleOutput.Line($"{asset.Sha256.Value}  {asset.Length,9}  {asset.Path}  ({asset.ContentType})");
        }

        ConsoleOutput.Line($"version {result.Value.Version}");
        if (result.Value.IsPlaceholder)
        {
            ConsoleOutput.Warn("These are placeholder assets: this binary was built without mysync-site and can't publish.");
            return ExitCodes.Unexpected;
        }

        return ExitCodes.Success;
    }, cancellationToken);
}
