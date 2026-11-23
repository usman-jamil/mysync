using ConsoleAppFramework;
using MySync.Application.Sync.Pull;
using MySync.Cli.Rendering;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync pull</c> (CLAUDE.md §10c): a read-only download of a share, or parts of it, into an ordinary folder.</summary>
internal sealed class PullCommands(CommandRunner runner)
{
    private static readonly Error PullNeedsShare = Error.Validation(
        "Cli.PullNeedsShare", "Name the share to pull from: mysync pull <name|shareId> [<share-path>...]");

    /// <summary>
    /// Download a share, or folders and files of it, into an ordinary folder. Never changes the
    /// share, keeps no sync state, and never replaces a local file unless asked.
    /// </summary>
    /// <param name="share">The share, by name or ID.</param>
    /// <param name="paths">Folders and files in the share (default: all of it).</param>
    /// <param name="to">The folder to put them in (default: this folder); created if missing.</param>
    /// <param name="latest">The newest pushed folder of each host (host/timestamp).</param>
    /// <param name="host">With --latest: only this host; repeatable.</param>
    /// <param name="overwrite">Replace local files whose content differs.</param>
    /// <param name="dryRun">-n, Show what would be downloaded, without writing anything.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("pull")]
    public Task<int> Pull(
        [Argument] string? share = null,
        string? to = null,
        bool latest = false,
        string[]? host = null,
        bool overwrite = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default,
        [Argument] params string[] paths) =>
        runner.RunAsync(async () =>
        {
            if (share is null)
            {
                return runner.Fail(PullNeedsShare);
            }

            Result<ShareReference> reference = ShareReference.Parse(share);
            if (reference.IsFailure)
            {
                return runner.Fail(reference.Error);
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            PullCommand command = new(
                reference.Value,
                [.. paths.Select(SharePathArgument.Normalize)],
                to,
                latest,
                host ?? [],
                overwrite,
                dryRun,
                runner.Options.Parallel);
            Result<PullReport> pulled = await SyncProgressDisplay.RunAsync(runner.Options, progress =>
                runner.SendAsync<PullCommand, PullReport>(command with { Progress = progress }, cancellationToken));
            if (pulled.IsFailure)
            {
                return runner.Fail(pulled.Error);
            }

            int exitCode = ExitCodeOf(pulled.Value);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(pulled.Value, exitCode);
            }

            PullReportView.Render(pulled.Value);
            return exitCode;
        }, cancellationToken);

    /// <summary>A pull never has conflicts, commits or deletes: 0, or 9 if paths were skipped.</summary>
    internal static int ExitCodeOf(PullReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Skipped.IsEmpty ? ExitCodes.Success : ExitCodes.PathsSkipped;
    }
}
