using System.Collections.Immutable;
using FluentValidation;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Shares.Clone;

/// <summary>
/// <c>mysync sync &lt;name|shareId&gt;</c> (CLAUDE.md §9): download a share into an empty folder. With
/// <c>--force</c> the folder may hold files; they are merged against an empty base, so nothing is
/// overwritten: same content is a no-op, different content a conflict, local-only files are uploaded.
/// </summary>
/// <param name="Share">The share's name or ID; a name is resolved through its alias.</param>
public sealed record CloneShareCommand(
    ShareReference Share,
    bool Force = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null) : ICommand<SyncReport>;

internal sealed class CloneShareCommandValidator : AbstractValidator<CloneShareCommand>
{
    public CloneShareCommandValidator()
    {
        RuleFor(command => command.Share).NotNull();

        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class CloneShareCommandHandler(
    IFolderLock folderLock,
    IShareStateStore stateStore,
    IShareStore store,
    ILocalFolder folder,
    ShareResolver resolver,
    SyncPipeline pipeline,
    SyncOptions options)
    : ICommandHandler<CloneShareCommand, SyncReport>
{
    // mysync's own artifacts don't make a folder "not empty", and nor does OS clutter (CLAUDE.md §9).
    private static readonly HashSet<string> OwnArtifacts = new(StateFileNames.AlwaysIgnored, StringComparer.OrdinalIgnoreCase);

    public async Task<Result<SyncReport>> Handle(CloneShareCommand command, CancellationToken cancellationToken)
    {
        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<SyncReport>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState?> existing = await stateStore.LoadAsync(cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure<SyncReport>(existing.Error);
        }

        if (existing.Value is { } state)
        {
            return Result.Failure<SyncReport>(ShareErrors.AlreadyAShare(state.ShareId));
        }

        if (!command.Force)
        {
            Result<IReadOnlyList<string>> names = await folder.ListTopLevelNamesAsync(cancellationToken);
            if (names.IsFailure)
            {
                return Result.Failure<SyncReport>(names.Error);
            }

            if (names.Value.Any(name => !OwnArtifacts.Contains(name) && !StateFileNames.IsOsClutter(name)))
            {
                return Result.Failure<SyncReport>(ShareErrors.FolderNotEmpty(folder.RootPath));
            }
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<SyncReport>(location.Error);
        }

        Result<ResolvedShare> resolved = await resolver.ResolveAsync(command.Share, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<SyncReport>(resolved.Error);
        }

        ShareId shareId = resolved.Value.ShareId;
        if (resolved.Value.Name is { } name)
        {
            // A name nobody finished creating: say so, rather than "share not found".
            Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
            if (head.IsFailure)
            {
                return Result.Failure<SyncReport>(head.Error);
            }

            if (head.Value is null)
            {
                return Result.Failure<SyncReport>(ShareErrors.Incomplete(name, shareId));
            }
        }

        // An empty base: everything remote is new here, everything local (with --force) is new there.
        Result<SyncReport> cloned = await pipeline.SynchronizeAsync(
            new SyncSession(
                shareId,
                location.Value,
                ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer),
                ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
                [],
                MergeOptions.Default,
                AllowMassDelete: false,
                DryRun: false,
                command.Progress ?? NullSyncProgressReporter.Instance,
                command.Parallelism ?? options.MaxParallelism),
            cancellationToken);

        return cloned.IsSuccess ? cloned.Value with { Name = resolved.Value.Name } : cloned;
    }
}
