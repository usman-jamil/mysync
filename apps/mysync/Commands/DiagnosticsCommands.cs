using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Diagnostics.SelfCheck;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

public class DiagnosticsCommands(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Verify this binary's runtime can do what mysync relies on (used by release smoke tests).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Command("self-check")]
    public async Task<int> SelfCheck(CancellationToken cancellationToken)
    {
        using IServiceScope scope = serviceProvider.CreateScope();

        IQueryHandler<RunSelfCheckQuery, SelfCheckResponse> handler =
            scope.ServiceProvider.GetRequiredService<IQueryHandler<RunSelfCheckQuery, SelfCheckResponse>>();

        Result<SelfCheckResponse> result = await handler.Handle(new RunSelfCheckQuery(), cancellationToken);

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
    }
}
