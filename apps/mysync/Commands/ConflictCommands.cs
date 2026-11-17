using ConsoleAppFramework;
using MySync.Application.Conflicts.List;
using MySync.Application.Conflicts.Resolve;
using MySync.Application.Sync.Run;
using MySync.Cli.Rendering;
using SharedKernel;
using Spectre.Console;

namespace MySync.Cli.Commands;

/// <summary><c>mysync conflicts</c> and <c>mysync resolve</c> (CLAUDE.md §10).</summary>
internal sealed class ConflictCommands(CommandRunner runner)
{
    /// <summary>List the open conflicts this folder's last sync recorded (works offline).</summary>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("conflicts")]
    public Task<int> Conflicts(CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result<ConflictListResponse> listed = await runner.QueryAsync<ListConflictsQuery, ConflictListResponse>(new ListConflictsQuery(), cancellationToken);
            if (listed.IsFailure)
            {
                return runner.Fail(listed.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(listed.Value, ExitCodes.Success);
            }

            if (listed.Value.Conflicts.IsEmpty)
            {
                AnsiConsole.MarkupLine("[green]No open conflicts.[/]");
            }
            else
            {
                SyncReportView.RenderConflicts(listed.Value.Conflicts);
            }

            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Decide conflicts: keep this folder's version (local) or the share's (remote). Only the named paths are touched.</summary>
    /// <param name="paths">The conflicted paths, relative to the share folder.</param>
    /// <param name="keep">local or remote.</param>
    /// <param name="all">Resolve every open conflict.</param>
    /// <param name="dryRun">-n, Show what would happen without changing anything.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("resolve")]
    public Task<int> Resolve(
        string? keep = null,
        bool all = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default,
        [Argument] params string[] paths) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            ResolveConflictsCommand command = new([.. paths.Select(SharePathArgument.Normalize)], all, KeepArgument(keep), dryRun, runner.Options.Parallel);
            Result<SyncReport> resolved = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
                runner.SendAsync<ResolveConflictsCommand, SyncReport>(command with { Progress = progress }, cancellationToken));
            if (resolved.IsFailure)
            {
                return runner.Fail(resolved.Error);
            }

            int exitCode = SyncCommands.ExitCodeOf(resolved.Value);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(resolved.Value, "resolve", exitCode);
            }

            SyncReportView.RenderResolve(resolved.Value);
            return exitCode;
        }, cancellationToken);

    // "Local" and "REMOTE" are fine too; anything else goes through as typed, for the validator to reject.
    private static string? KeepArgument(string? keep) => keep switch
    {
        _ when string.Equals(keep, "local", StringComparison.OrdinalIgnoreCase) => "local",
        _ when string.Equals(keep, "remote", StringComparison.OrdinalIgnoreCase) => "remote",
        _ => keep,
    };
}

/// <summary>A path as a user types it, to the form a share path takes: <c>/</c>-separated, no <c>./</c>.</summary>
internal static class SharePathArgument
{
    public static string Normalize(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        // On Windows people type backslashes; elsewhere a backslash is part of a name and must stay (and be rejected).
        string path = OperatingSystem.IsWindows() ? typed.Replace('\\', '/') : typed;
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.TrimEnd('/');
    }
}
