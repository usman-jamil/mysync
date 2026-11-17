using FluentValidation;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Sync.Run;

/// <summary><c>mysync sync</c> in a folder that is already a share: two-way sync (CLAUDE.md §9).</summary>
/// <param name="Parallelism"><c>--parallel</c>; <c>null</c> uses <see cref="SyncOptions.MaxParallelism"/>.</param>
/// <param name="Progress">Supplied by the command for this run only; <c>null</c> reports nothing.</param>
public sealed record RunSyncCommand(
    bool DryRun = false,
    bool NoDelete = false,
    bool AllowMassDelete = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null) : ICommand<SyncReport>;

internal sealed class RunSyncCommandValidator : AbstractValidator<RunSyncCommand>
{
    public RunSyncCommandValidator() => RuleFor(command => command.Parallelism).ValidParallelism();
}

internal sealed class RunSyncCommandHandler(
    IFolderLock folderLock,
    IShareStateStore stateStore,
    IShareStore store,
    SyncPipeline pipeline,
    SyncOptions options)
    : ICommandHandler<RunSyncCommand, SyncReport>
{
    public async Task<Result<SyncReport>> Handle(RunSyncCommand command, CancellationToken cancellationToken)
    {
        // Phase 1: lock, load and validate the state, check the environment points at the same place.
        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<SyncReport>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SyncReport>(loaded.Error);
        }

        if (loaded.Value is not { } state)
        {
            return Result.Failure<SyncReport>(ShareErrors.NotAShare());
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<SyncReport>(location.Error);
        }

        Result sameStorage = state.CheckLocation(location.Value);
        if (sameStorage.IsFailure)
        {
            return Result.Failure<SyncReport>(sameStorage.Error);
        }

        return await pipeline.SynchronizeAsync(
            new SyncSession(
                state.ShareId,
                state.Location,
                state.Base,
                state.HashCache,
                state.Conflicts,
                new MergeOptions(command.NoDelete),
                command.AllowMassDelete,
                command.DryRun,
                command.Progress ?? NullSyncProgressReporter.Instance,
                command.Parallelism ?? options.MaxParallelism),
            cancellationToken);
    }
}
