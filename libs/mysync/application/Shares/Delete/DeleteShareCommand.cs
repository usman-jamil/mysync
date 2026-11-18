using System.Collections.Immutable;
using FluentValidation;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Logging;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Shares.Delete;

/// <summary>
/// <c>mysync delete [&lt;shareId&gt;]</c>: remove a share for good, e.g. sensitive data after a work
/// session. Confirmation (<c>--yes</c>) is the command's job and never reaches this layer.
/// </summary>
/// <remarks>
/// In this order, so every machine learns at once and an interruption is safe to re-run:
/// <list type="number">
/// <item>Replace HEAD with the deleted marker (<see cref="Manifest.CreateDeletedRevision"/>), conditionally,
/// as the next revision. Any sync in progress elsewhere fails its commit, re-reads, and stops.</item>
/// <item>Delete every object, then every history manifest (they list file paths).</item>
/// <item>Report copies the provider still keeps (soft delete, versions).</item>
/// <item>If this folder is that share: remove <c>.mysynctool</c> and <c>.mysynctool.log</c>. Files stay.</item>
/// </list>
/// A share already marked deleted is swept again, so re-running finishes an interrupted delete.
/// </remarks>
/// <param name="ShareId">As typed; <c>null</c> means this folder's share.</param>
public sealed record DeleteShareCommand(string? ShareId = null, int? Parallelism = null) : ICommand<DeleteShareResponse>;

/// <param name="WasAlreadyDeleted">The marker was there already: this run finished an earlier delete.</param>
/// <param name="HistoryRevisionsSwept">History manifests up to this revision were removed (missing ones count as removed).</param>
/// <param name="LocalStateRemoved">This folder was the share, and its <c>.mysynctool</c> and log are gone.</param>
/// <param name="RecoverableCopies">Deleted blobs the provider can still restore, until its retention ends; -1 if it couldn't say.</param>
public sealed record DeleteShareResponse(
    ShareId ShareId,
    Revision MarkerRevision,
    ShareDeletion Deletion,
    bool WasAlreadyDeleted,
    int ObjectsDeleted,
    long BytesDeleted,
    long HistoryRevisionsSwept,
    bool LocalStateRemoved,
    int RecoverableCopies,
    ImmutableArray<string> Warnings);

internal sealed class DeleteShareCommandValidator : AbstractValidator<DeleteShareCommand>
{
    public DeleteShareCommandValidator()
    {
        RuleFor(command => command.ShareId)
            .Must(id => id is null || Domain.Shares.ShareId.TryParse(id, out _))
            .WithErrorCode(ShareErrors.InvalidId().Code)
            .WithMessage(ShareErrors.InvalidId().Description);

        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class DeleteShareCommandHandler(
    IFolderLock folderLock,
    IShareStateStore stateStore,
    IShareStore store,
    ILocalFolder folder,
    IConflictLog conflictLog,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    RemoteCommitter committer,
    SyncOptions options,
    TimeProvider timeProvider,
    ILogger<DeleteShareCommandHandler> logger)
    : ICommandHandler<DeleteShareCommand, DeleteShareResponse>
{
    public async Task<Result<DeleteShareResponse>> Handle(DeleteShareCommand command, CancellationToken cancellationToken)
    {
        // Which share, and is it this folder's?
        ShareState? here = null;
        if (command.ShareId is null)
        {
            Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
            if (loaded.IsFailure)
            {
                return Result.Failure<DeleteShareResponse>(loaded.Error);
            }

            here = loaded.Value;
            if (here is null)
            {
                return Result.Failure<DeleteShareResponse>(ShareErrors.NotAShare());
            }
        }
        else
        {
            // Deleting by ID works from any folder; a damaged state file here only means it isn't touched.
            Result<ShareState?> loaded = await stateStore.LoadAsync(cancellationToken);
            here = loaded.IsSuccess ? loaded.Value : null;
        }

        ShareId shareId = command.ShareId is null ? here!.ShareId : ParseValidated(command.ShareId);
        bool thisFolder = here is not null && here.ShareId == shareId;

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<DeleteShareResponse>(location.Error);
        }

        IAsyncDisposable? held = null;
        try
        {
            if (thisFolder)
            {
                Result sameStorage = here!.CheckLocation(location.Value);
                if (sameStorage.IsFailure)
                {
                    return Result.Failure<DeleteShareResponse>(sameStorage.Error);
                }

                Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
                if (locked.IsFailure)
                {
                    return Result.Failure<DeleteShareResponse>(locked.Error);
                }

                held = locked.Value;
            }

            Result<DeleteShareResponse> deleted = await DeleteAsync(shareId, thisFolder, command.Parallelism ?? options.MaxParallelism, cancellationToken);
            if (held is not null)
            {
                await held.DisposeAsync();
                held = null;
            }

            return await RemoveLockFileAfterAsync(deleted, cancellationToken);
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }
    }

    private async Task<Result<DeleteShareResponse>> RemoveLockFileAfterAsync(Result<DeleteShareResponse> result, CancellationToken cancellationToken)
    {
        // Released first, then removed: an ordinary folder has no lock file.
        if (result is { IsSuccess: true, Value.LocalStateRemoved: true })
        {
            await folderLock.RemoveAsync(cancellationToken);
        }

        return result;
    }

    private static ShareId ParseValidated(string id) =>
        Domain.Shares.ShareId.TryParse(id, out ShareId? parsed) ? parsed : throw new ArgumentException("Not validated.", nameof(id));

    private async Task<Result<DeleteShareResponse>> DeleteAsync(ShareId shareId, bool thisFolder, int parallelism, CancellationToken cancellationToken)
    {
        // 1. The marker: from here on every machine sees the share as deleted.
        Result<(Manifest Marker, bool Already)> marked = await MarkDeletedAsync(shareId, cancellationToken);
        if (marked.IsFailure)
        {
            return Result.Failure<DeleteShareResponse>(marked.Error);
        }

        Manifest marker = marked.Value.Marker;
        List<string> warnings = [];

        // 2. Objects, then history. Listing fully first, so deleting doesn't disturb the listing.
        List<StoredObjectInfo> objects = [];
        await foreach (Result<StoredObjectInfo> item in store.ListObjectsAsync(shareId, cancellationToken))
        {
            if (item.IsFailure)
            {
                return Result.Failure<DeleteShareResponse>(item.Error);
            }

            objects.Add(item.Value);
        }

        Result swept = await ForEachAsync(objects, item => store.DeleteObjectAsync(shareId, item.Hash, cancellationToken), parallelism, cancellationToken);
        if (swept.IsFailure)
        {
            return Result.Failure<DeleteShareResponse>(swept.Error);
        }

        // Every revision before the marker may have a history copy; a missing one counts as deleted.
        long lastRevision = marker.Revision.Value;
        IEnumerable<Revision> revisions = Enumerable.Range(1, (int)Math.Min(lastRevision, int.MaxValue)).Select(value => Rev(value));
        Result history = await ForEachAsync(revisions, revision => store.DeleteManifestHistoryAsync(shareId, revision, cancellationToken), parallelism, cancellationToken);
        if (history.IsFailure)
        {
            return Result.Failure<DeleteShareResponse>(history.Error);
        }

        // 3. What the provider can still bring back.
        Result<int> recoverable = await store.CountRecoverableCopiesAsync(shareId, cancellationToken);
        if (recoverable.IsFailure)
        {
            logger.LogWarning("Could not check for recoverable copies: {Error}", recoverable.Error.Code);
            warnings.Add("Could not check whether the storage account keeps deleted copies (soft delete or versioning).");
        }

        // 4. This folder becomes an ordinary folder; its files stay.
        if (thisFolder)
        {
            await RemoveLocalStateAsync(warnings, cancellationToken);
        }

        return new DeleteShareResponse(
            shareId,
            marker.Revision,
            marker.Deletion!,
            marked.Value.Already,
            objects.Count,
            objects.Sum(item => item.Size),
            lastRevision,
            thisFolder,
            recoverable.IsSuccess ? recoverable.Value : -1,
            [.. warnings]);
    }

    // Conditional on HEAD, like any commit: another machine committing first means read again.
    private async Task<Result<(Manifest Marker, bool Already)>> MarkDeletedAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= options.MaxCommitAttempts; attempt++)
        {
            Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
            if (head.IsFailure)
            {
                return Result.Failure<(Manifest, bool)>(head.Error);
            }

            if (head.Value is not { } snapshot)
            {
                return Result.Failure<(Manifest, bool)>(ShareErrors.NotFound(shareId));
            }

            if (snapshot.Manifest.IsDeleted)
            {
                return (snapshot.Manifest, true);
            }

            Result<Manifest> marker = snapshot.Manifest.CreateDeletedRevision(new DateTimeOffset(clock.UtcNow, TimeSpan.Zero), machine.Name);
            if (marker.IsFailure)
            {
                return Result.Failure<(Manifest, bool)>(marker.Error);
            }

            Result<string> replaced = await store.ReplaceManifestAsync(shareId, marker.Value, snapshot.ETag, cancellationToken);
            if (replaced.IsSuccess)
            {
                return (marker.Value, false);
            }

            if (replaced.Error.Code != "Storage.PreconditionFailed")
            {
                return Result.Failure<(Manifest, bool)>(replaced.Error);
            }

            if (attempt < options.MaxCommitAttempts)
            {
                await Task.Delay(committer.BackOff(attempt), timeProvider, cancellationToken);
            }
        }

        return Result.Failure<(Manifest, bool)>(SyncErrors.CommitRetriesExhausted(options.MaxCommitAttempts));
    }

    private async Task RemoveLocalStateAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        foreach ((Result removed, string what) in (IEnumerable<(Result, string)>)
        [
            (await stateStore.DeleteAsync(cancellationToken), StateFileNames.State),
            (await conflictLog.DeleteAsync(cancellationToken), StateFileNames.ConflictLog),
            (await folder.CleanStagingAsync(cancellationToken), StateFileNames.StagingDirectory),
        ])
        {
            if (removed.IsFailure)
            {
                warnings.Add($"The share is deleted, but {what} could not be removed from this folder: {removed.Error.Description}");
            }
        }
    }

    private static async Task<Result> ForEachAsync<T>(IEnumerable<T> items, Func<T, Task<Result>> delete, int parallelism, CancellationToken cancellationToken)
    {
        Error? first = null;
        Lock gate = new();
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (item, _) =>
            {
                Result result = await delete(item);
                if (result.IsFailure)
                {
                    lock (gate)
                    {
                        first ??= result.Error;
                    }
                }
            });

        return first is null ? Result.Success() : Result.Failure(first);
    }

    private static Revision Rev(long value) => Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentOutOfRangeException(nameof(value));
}
