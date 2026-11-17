using ConsoleAppFramework;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Diagnostics.SelfCheck;
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
}
