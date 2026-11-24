using FluentValidation;
using MySync.Application.Abstractions.Composition;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Clone;
using MySync.Application.Sync;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Shares.Clone;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.CloneInto;

/// <summary>
/// <c>mysync clone &lt;share&gt; [&lt;folder&gt;]</c> (CLAUDE.md §9, "<c>mysync clone</c>"): pick or
/// create the folder, then run the existing clone (<see cref="CloneShareCommand"/>) there. If the
/// clone fails or is cancelled, what this command created is taken away again, and nothing else.
/// </summary>
/// <param name="Share">The share's name or ID.</param>
/// <param name="Folder">Where to clone, absolute or relative to the current folder; <c>null</c> for a new folder named after the share.</param>
/// <param name="CleanedUp">Told what the clean-up after a failed or cancelled clone removed and left behind (a cancellation has no other way to say it).</param>
public sealed record CloneIntoFolderCommand(
    ShareReference Share,
    string? Folder = null,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null,
    Action<CloneCleanUpReport>? CleanedUp = null) : ICommand<CloneIntoFolderResponse>;

/// <param name="Report">The clone's report, as <c>mysync sync &lt;share&gt;</c> gives it.</param>
/// <param name="Folder">The folder cloned into, full.</param>
/// <param name="CreatedFolder">Whether this command created it (else it existed and was empty).</param>
public sealed record CloneIntoFolderResponse(SyncReport Report, string Folder, bool CreatedFolder);

/// <param name="Folder">The target folder.</param>
/// <param name="FolderRemoved">Whether the target is gone (it was created by this command and is empty again).</param>
/// <param name="LeftBehind">What is still there that the clean-up didn't remove, relative to the target: something this command didn't write, or a file it couldn't delete (with why).</param>
public sealed record CloneCleanUpReport(string Folder, bool FolderRemoved, IReadOnlyList<string> LeftBehind);

internal sealed class CloneIntoFolderCommandValidator : AbstractValidator<CloneIntoFolderCommand>
{
    public CloneIntoFolderCommandValidator()
    {
        RuleFor(command => command.Share).NotNull();

        RuleFor(command => command.Folder)
            .Must(folder => folder is null || !string.IsNullOrWhiteSpace(folder))
            .WithMessage("The folder to clone into can't be empty.");

        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class CloneIntoFolderCommandHandler(
    IShareStore store,
    IAliasStore aliases,
    ShareResolver resolver,
    ICloneFolders folders,
    ILocalSources sources,
    ICloneRunner runner)
    : ICommandHandler<CloneIntoFolderCommand, CloneIntoFolderResponse>
{
    // Like the clone's own check: mysync's artifacts and OS clutter don't make a folder "not empty".
    private static readonly HashSet<string> OwnArtifacts = new(StateFileNames.AlwaysIgnored, StringComparer.OrdinalIgnoreCase);

    public async Task<Result<CloneIntoFolderResponse>> Handle(CloneIntoFolderCommand command, CancellationToken cancellationToken)
    {
        // The share first: an unknown name, a deleted or unfinished share, credentials or
        // "upgrade mysync" fail here, before anything on disk is touched.
        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<CloneIntoFolderResponse>(location.Error);
        }

        Result<ClonedShare> share = await ReadShareAsync(command, cancellationToken);
        if (share.IsFailure)
        {
            return Result.Failure<CloneIntoFolderResponse>(share.Error);
        }

        Result<string> target = CloneTarget.Choose(command.Share, share.Value.Name, command.Folder, folders.CurrentFolder);
        if (target.IsFailure)
        {
            return Result.Failure<CloneIntoFolderResponse>(target.Error);
        }

        Result<CloneFolderProbe> probe = await CheckTargetAsync(target.Value, cancellationToken);
        if (probe.IsFailure)
        {
            return Result.Failure<CloneIntoFolderResponse>(probe.Error);
        }

        Result<IReadOnlyList<string>> created = folders.Create(target.Value);
        if (created.IsFailure)
        {
            return Result.Failure<CloneIntoFolderResponse>(created.Error);
        }

        // By ID: a name re-pointed meanwhile must not switch the share under this clone.
        CloneShareCommand clone = new(ShareReference.For(share.Value.ShareId), Force: false, command.Parallelism, command.Progress);
        Result<SyncReport> cloned;
        try
        {
            cloned = await runner.CloneAsync(target.Value, clone, cancellationToken);
        }
#pragma warning disable CA1031 // Not handled: cleaned up after (Ctrl+C, or an unexpected failure), then rethrown.
        catch (Exception)
#pragma warning restore CA1031
        {
            await CleanUpAsync(command, share.Value, target.Value, probe.Value, created.Value);
            throw;
        }

        if (cloned.IsFailure)
        {
            await CleanUpAsync(command, share.Value, target.Value, probe.Value, created.Value);
            return Result.Failure<CloneIntoFolderResponse>(cloned.Error);
        }

        // Skipped paths and warnings are a success: nothing is taken away.
        return new CloneIntoFolderResponse(cloned.Value with { Name = share.Value.Name }, target.Value, created.Value.Count > 0);
    }

    private async Task<Result<ClonedShare>> ReadShareAsync(CloneIntoFolderCommand command, CancellationToken cancellationToken)
    {
        Result<ResolvedShare> resolved = await resolver.ResolveAsync(command.Share, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<ClonedShare>(resolved.Error);
        }

        ShareId shareId = resolved.Value.ShareId;
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<ClonedShare>(head.Error);
        }

        if (head.Value is not { } snapshot)
        {
            return resolved.Value.Name is { } unfinished
                ? Result.Failure<ClonedShare>(ShareErrors.Incomplete(unfinished, shareId))
                : Result.Failure<ClonedShare>(ShareErrors.NotFound(shareId));
        }

        if (snapshot.Manifest.Deletion is { } deletion)
        {
            return Result.Failure<ClonedShare>(ShareErrors.Deleted(shareId, deletion));
        }

        ShareName? name = resolved.Value.Name;
        if (name is null && command.Folder is null)
        {
            // Named after the share: by ID, its first name (several after an interrupted rename).
            Result<IReadOnlyList<AliasRecord>> names = await aliases.FindByShareAsync(shareId, cancellationToken);
            if (names.IsFailure)
            {
                return Result.Failure<ClonedShare>(names.Error);
            }

            name = CloneTarget.FirstName(names.Value.Select(alias => alias.Name));
        }

        return new ClonedShare(shareId, name, snapshot.Manifest);
    }

    private async Task<Result<CloneFolderProbe>> CheckTargetAsync(string target, CancellationToken cancellationToken)
    {
        Result<CloneFolderProbe> probe = folders.Inspect(target);
        if (probe.IsFailure)
        {
            return probe;
        }

        if (probe.Value.Kind == CloneFolderKind.File)
        {
            return Result.Failure<CloneFolderProbe>(CloneErrors.TargetIsAFile(target));
        }

        // A share folder inside another share would be uploaded as that share's files.
        Result<IReadOnlyList<SyncedFolder>> synced = await sources.FindSyncedFoldersAsync(probe.Value.NearestExisting, cancellationToken);
        if (synced.IsFailure)
        {
            return Result.Failure<CloneFolderProbe>(synced.Error);
        }

        if (synced.Value.Count > 0)
        {
            SyncedFolder found = synced.Value[0];
            bool isTheTarget = string.Equals(Path.TrimEndingDirectorySeparator(found.FullPath), target, StringComparison.Ordinal);
            return Result.Failure<CloneFolderProbe>(CloneErrors.InsideAShare(found.FullPath, found.ShareId, isTheTarget));
        }

        if (probe.Value.IsUnsafeRoot)
        {
            return Result.Failure<CloneFolderProbe>(CloneErrors.UnsafeRoot(target));
        }

        if (probe.Value.Names.Any(name => !OwnArtifacts.Contains(name) && !StateFileNames.IsOsClutter(name)))
        {
            return Result.Failure<CloneFolderProbe>(CloneErrors.FolderNotEmpty(target));
        }

        return probe;
    }

    private async Task CleanUpAsync(CloneIntoFolderCommand command, ClonedShare share, string target, CloneFolderProbe before, IReadOnlyList<string> created)
    {
        // What the clone may have written: the HEAD read before it started, and HEAD now (it may have
        // moved on before the clone read it). Best effort; never cancelled half-way.
        List<Manifest> manifests = [share.Head];
        Result<ManifestSnapshot?> now = await store.GetManifestAsync(share.ShareId, CancellationToken.None);
        if (now.IsSuccess && now.Value is { } snapshot)
        {
            manifests.Add(snapshot.Manifest);
        }

        CloneCleanUpReport report = await new CloneCleanUp(folders).RunAsync(target, before.Names, created, manifests);
        command.CleanedUp?.Invoke(report);
    }

    private sealed record ClonedShare(ShareId ShareId, ShareName? Name, Manifest Head);
}
