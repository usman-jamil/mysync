using ConsoleAppFramework;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Conflicts.List;
using MySync.Application.Conflicts.Resolve;
using MySync.Application.Sync.Run;
using MySync.Cli.Rendering;
using MySync.Domain.Diffing;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using SharedKernel;
using Spectre.Console;

namespace MySync.Cli.Commands;

/// <summary><c>mysync conflicts</c>, <c>diff</c> and <c>resolve</c> (CLAUDE.md §10).</summary>
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
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("To see one conflict:");
                foreach (Conflict conflict in listed.Value.Conflicts.OrderBy(open => open.Path, SharePath.OrdinalComparer))
                {
                    AnsiConsole.MarkupLine($"  {ConsoleOutput.Value(DiffView.DiffCommand(conflict.Path))}");
                }
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

    /// <summary>What differs between this folder's version of a file and the share's, ending with the resolve commands to paste.</summary>
    /// <param name="paths">Paths to compare (default: every open conflict).</param>
    /// <param name="base">Show base → local and base → remote, what each side changed (modify/modify).</param>
    /// <param name="context">-U, Lines of context around each change (0-1000).</param>
    /// <param name="stat">One line per path with counts, no hunks.</param>
    /// <param name="print">Write one version's raw bytes to stdout: local, remote or base (one path).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("diff")]
    public Task<int> Diff(
        bool @base = false,
        int context = DiffLimits.DefaultContext,
        bool stat = false,
        string? print = null,
        CancellationToken cancellationToken = default,
        [Argument] params string[] paths) =>
        runner.RunAsync(async () =>
        {
            string[] typed = [.. paths.Select(SharePathArgument.Normalize)];
            if (print is not null)
            {
                return await PrintAsync(print, typed, @base || stat, cancellationToken);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<DiffResponse> diff = await runner.QueryAsync<DiffQuery, DiffResponse>(new DiffQuery(typed, context, @base, stat), cancellationToken);
            if (diff.IsFailure)
            {
                return runner.Fail(diff.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(diff.Value, stat, ExitCodes.Success);
            }

            DiffView.Render(diff.Value, stat);
            return ExitCodes.Success;
        }, cancellationToken);

    private async Task<int> PrintAsync(string print, string[] paths, bool combined, CancellationToken cancellationToken)
    {
        DiffSideKind? side = print.ToUpperInvariant() switch
        {
            "LOCAL" => DiffSideKind.Local,
            "REMOTE" => DiffSideKind.Remote,
            "BASE" => DiffSideKind.Base,
            _ => null,
        };
        if (side is null)
        {
            return runner.Fail(InvalidPrint);
        }

        if (combined || runner.Options.Json)
        {
            return runner.Fail(DiffErrors.PrintCombined);
        }

        if (paths.Length != 1)
        {
            return runner.Fail(DiffErrors.PrintNeedsOnePath);
        }

        if (side != DiffSideKind.Local)
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }
        }

        Result<Stream> opened = await runner.QueryAsync<PrintVersionQuery, Stream>(new PrintVersionQuery(paths[0], side.Value), cancellationToken);
        if (opened.IsFailure)
        {
            return runner.Fail(opened.Error);
        }

        // Raw bytes, straight to the stdout handle: no encoding, no markup, no line-ending changes.
        await using Stream source = opened.Value;
        await using Stream stdout = Console.OpenStandardOutput();
        try
        {
            await source.CopyToAsync(stdout, cancellationToken);
        }
        catch (IOException exception)
        {
            return runner.Fail(Error.Problem("Storage.HashMismatch", exception.Message));
        }

        return ExitCodes.Success;
    }

    private static readonly Error InvalidPrint = Error.Validation("Diff.InvalidPrint", "--print takes local, remote or base.");

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
