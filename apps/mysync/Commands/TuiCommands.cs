using System.Collections.Immutable;
using ConsoleAppFramework;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.State;
using MySync.Application.Session;
using MySync.Application.Shares.Preview;
using MySync.Cli.Hosting;
using MySync.Cli.Rendering;
using MySync.Domain.Session;
using MySync.Domain.Shares;
using MySync.Tui.Rendering;
using MySync.Tui.Runtime;
using MySync.Tui.State;
using MySync.Tui.Terminal;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary>
/// <c>mysync tui</c> (CLAUDE.md §10d): the start-up checks, then the terminal interface over this
/// folder's session. Holds no screen code: everything on screen is <c>MySync.Tui</c>'s.
/// </summary>
internal sealed class TuiCommands(CommandRunner runner, IServiceProvider services, SwitchableLogSink logSink)
{
    /// <summary>
    /// Open the terminal interface for this folder: every file's status; rescan, sync, conflicts
    /// and push, with live progress.
    /// </summary>
    /// <param name="name">In a folder that isn't a share yet: create an empty one with this name first.</param>
    /// <param name="compress">With --name: the new share stores its files compressed.</param>
    /// <param name="ascii">Draw with ASCII symbols only (automatic when the output isn't UTF-8).</param>
    /// <param name="noColor">No colours (NO_COLOR does the same); bold and reverse video only.</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("tui")]
    public Task<int> Tui(string? name = null, bool compress = false, bool ascii = false, bool noColor = false, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return runner.Fail(TuiErrors.NotATerminal());
            }

            if (Console.WindowWidth < 80 || Console.WindowHeight < 24)
            {
                return runner.Fail(TuiErrors.TooSmall(Console.WindowWidth, Console.WindowHeight));
            }

            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            // One interface per folder, taken before a share is created, so two can't both create one.
            Result<IAsyncDisposable> locked = await services.GetRequiredService<IInterfaceLock>().AcquireAsync(cancellationToken);
            if (locked.IsFailure)
            {
                return runner.Fail(locked.Error);
            }

            await using IAsyncDisposable held = locked.Value;

            Result<ImmutableArray<string>> started = await PrepareShareAsync(name, compress, cancellationToken);
            if (started.IsFailure)
            {
                int failed = runner.Fail(started.Error);
                if (started.Error.Code == "Shares.NameRequired")
                {
                    ConsoleOutput.Info("To work on an existing share instead, run mysync sync <name> in an empty folder first, then mysync tui there.");
                }

                return failed;
            }

            return await RunScreenAsync(ascii, noColor, started.Value, cancellationToken);
        }, cancellationToken);

    /// <returns>Notes for the activity list (ignored options, a new share).</returns>
    private async Task<Result<ImmutableArray<string>>> PrepareShareAsync(string? name, bool compress, CancellationToken cancellationToken)
    {
        Result<NewSharePreview> preview = await runner.QueryAsync<PreviewNewShareQuery, NewSharePreview>(new PreviewNewShareQuery(CountFiles: false), cancellationToken);
        if (preview.IsFailure)
        {
            return Result.Failure<ImmutableArray<string>>(preview.Error);
        }

        if (preview.Value.ExistingShareId is not null)
        {
            List<string> notes = [];
            if (name is not null)
            {
                notes.Add($"--name is ignored: this folder is already a share. To rename it: mysync rename {name}");
            }

            if (compress)
            {
                notes.Add("--compress is ignored: compression is a setting of the share. Turn it on with: mysync compression on");
            }

            return Result.Success<ImmutableArray<string>>([.. notes]);
        }

        if (name is null)
        {
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory));
            return Result.Failure<ImmutableArray<string>>(ShareErrors.NameRequired(ShareName.SuggestFrom(folder)));
        }

        Result<ShareName> shareName = ShareName.Create(name);
        if (shareName.IsFailure)
        {
            return Result.Failure<ImmutableArray<string>>(shareName.Error);
        }

        Result<InitializeEmptyShareResponse> created = await runner.SendAsync<InitializeEmptyShareCommand, InitializeEmptyShareResponse>(
            new InitializeEmptyShareCommand(shareName.Value, compress), cancellationToken);
        return created.IsFailure
            ? Result.Failure<ImmutableArray<string>>(created.Error)
            : Result.Success<ImmutableArray<string>>([$"Created the share {created.Value.Name} ({created.Value.ShareId}). Nothing is uploaded until you press s."]);
    }

    private async Task<int> RunScreenAsync(bool ascii, bool noColor, ImmutableArray<string> notes, CancellationToken cancellationToken)
    {
        IMachineIdentity machine = services.GetRequiredService<IMachineIdentity>();
        ColorDepth colors = TerminalDetection.Colors(Environment.GetEnvironmentVariable, noColor, OperatingSystem.IsWindows());
        TuiOptions options = new(
            ascii || !TerminalDetection.Unicode(Console.OutputEncoding),
            colors,
            Environment.CurrentDirectory,
            machine.HostName,
            machine.Name,
            SyncCommands.LargeFileCount,
            SyncCommands.LargeTotalBytes,
            notes);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true; // the terminal is given back first
            stop.Cancel();
        });

        using SwitchableLogSink.Capture log = logSink.Hold();
        await using IFolderSession session = services.GetRequiredService<IFolderSession>();
        TuiRuntime runtime = new(new AnsiTerminal(new SystemConsole(), colors), session, options, TimeProvider.System, log.Drain);
        return await runtime.RunAsync(stop.Token);
    }
}
