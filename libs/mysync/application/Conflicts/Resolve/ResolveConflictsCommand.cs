using FluentValidation;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.State;
using MySync.Application.Shares;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Conflicts.Resolve;

/// <summary>
/// <c>mysync resolve &lt;path&gt;... --keep local|remote</c> or <c>resolve --all --keep ...</c>
/// (CLAUDE.md §10): the only way a conflict is decided. Runs the normal pipeline for the named
/// conflicted paths only, every other path is left as it is.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>--keep local</c> uploads the local version (or records the local delete) and commits it.</item>
/// <item><c>--keep remote</c> downloads the remote version over the local file (or deletes it), after
/// the usual re-check that the local file hasn't changed since the scan.</item>
/// <item>If a path's remote side changed again since the conflict was recorded, it stays a conflict
/// and is reported; it is never forced.</item>
/// <item>A resolved path gets the resolved state as its base, leaves <c>conflicts</c>, and gets a
/// <c>RESOLVED</c> line in <c>.mysynctool.log</c>.</item>
/// </list>
/// </remarks>
/// <param name="Paths">Relative to the share root, with <c>/</c>. Empty with <paramref name="All"/>.</param>
/// <param name="Keep"><c>local</c> or <c>remote</c>, as typed; the validator checks it.</param>
public sealed record ResolveConflictsCommand(
    IReadOnlyList<string> Paths,
    bool All,
    string? Keep,
    bool DryRun = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null) : ICommand<SyncReport>;

internal sealed class ResolveConflictsCommandValidator : AbstractValidator<ResolveConflictsCommand>
{
    public ResolveConflictsCommandValidator()
    {
        RuleFor(command => command.Keep)
            .Must(keep => keep is "local" or "remote")
            .WithErrorCode("Conflicts.InvalidKeep")
            .WithMessage("--keep must be 'local' or 'remote'.");

        RuleFor(command => command)
            .Must(command => command.All != command.Paths.Count > 0)
            .WithErrorCode("Conflicts.PathsOrAll")
            .WithMessage("Name the conflicted paths to resolve, or pass --all (not both).");

        RuleForEach(command => command.Paths)
            .Must(path => SharePath.Create(path).IsSuccess)
            .WithErrorCode("Conflicts.InvalidPath")
            .WithMessage((_, path) => $"'{PathErrors.Display(path)}' is not a path inside the share (use / and no leading /).");

        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class ResolveConflictsCommandHandler(
    IFolderLock folderLock,
    ShareFolder shareFolder,
    SyncPipeline pipeline,
    SyncOptions options)
    : ICommandHandler<ResolveConflictsCommand, SyncReport>
{
    public async Task<Result<SyncReport>> Handle(ResolveConflictsCommand command, CancellationToken cancellationToken)
    {
        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<SyncReport>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState> loaded = await shareFolder.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SyncReport>(loaded.Error);
        }

        ShareState state = loaded.Value;
        KeepSide keep = command.Keep == "local" ? KeepSide.Local : KeepSide.Remote;
        List<SharePath> named = command.All
            ? [.. state.Conflicts.Select(conflict => conflict.Path)]
            : [.. command.Paths.Select(path => SharePath.Create(path).Value).Distinct()];

        string[] notInConflict = [.. named.Where(path => !state.Conflicts.Any(conflict => conflict.Path == path)).Select(path => path.Value)];
        if (notInConflict.Length > 0)
        {
            return Result.Failure<SyncReport>(SyncErrors.NotInConflict(notInConflict));
        }

        var keepByPath = named.ToDictionary(path => path, _ => keep);

        return await pipeline.SynchronizeAsync(
            new SyncSession(
                state.ShareId,
                state.Location,
                state.Base,
                state.HashCache,
                state.Conflicts,
                MergeOptions.Default,
                AllowMassDelete: true, // only the named paths change, and the user named them
                command.DryRun,
                command.Progress ?? NullSyncProgressReporter.Instance,
                command.Parallelism ?? options.MaxParallelism,
                new ResolveRequest(keepByPath)),
            cancellationToken);
    }
}
