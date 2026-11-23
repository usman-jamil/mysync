using System.Collections.Immutable;
using FluentValidation;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares;
using MySync.Application.Sync.Pipeline;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Session;

/// <summary>
/// <c>mysync tui --name &lt;name&gt;</c> in a folder that isn't a share (CLAUDE.md §10d): an
/// <b>empty</b> share, so the screen can open at once. The name is claimed first, revision 1 is
/// committed with no entries, and <c>.mysynctool</c> gets an empty base. Nothing is scanned or
/// uploaded: the folder's files then show as new here, and the first sync uploads them.
/// </summary>
/// <param name="Compress">Start the share with compression on (revision 1 in the compressed format).</param>
public sealed record InitializeEmptyShareCommand(ShareName Name, bool Compress = false) : ICommand<InitializeEmptyShareResponse>;

public sealed record InitializeEmptyShareResponse(ShareId ShareId, ShareName Name, CompressionSetting Compression);

internal sealed class InitializeEmptyShareCommandValidator : AbstractValidator<InitializeEmptyShareCommand>
{
    public InitializeEmptyShareCommandValidator() => RuleFor(command => command.Name).NotNull();
}

internal sealed class InitializeEmptyShareCommandHandler(
    IFolderLock folderLock,
    IShareStateStore stateStore,
    IShareStore store,
    ILocalFolder folder,
    IAliasStore aliases,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    RemoteCommitter committer)
    : ICommandHandler<InitializeEmptyShareCommand, InitializeEmptyShareResponse>
{
    public async Task<Result<InitializeEmptyShareResponse>> Handle(InitializeEmptyShareCommand command, CancellationToken cancellationToken)
    {
        Result<IAsyncDisposable> locked = await folderLock.AcquireAsync(cancellationToken);
        if (locked.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(locked.Error);
        }

        await using IAsyncDisposable held = locked.Value;

        Result<ShareState?> existing = await stateStore.LoadAsync(cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(existing.Error);
        }

        if (existing.Value is { } state)
        {
            return Result.Failure<InitializeEmptyShareResponse>(ShareErrors.AlreadyAShare(state.ShareId));
        }

        if (folder.IsUnsafeRoot)
        {
            return Result.Failure<InitializeEmptyShareResponse>(ShareErrors.UnsafeRoot(folder.RootPath));
        }

        Result<StorageLocation> location = store.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(location.Error);
        }

        Result ready = await store.EnsureReadyForNewShareAsync(cancellationToken);
        if (ready.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(ready.Error);
        }

        var shareId = ShareId.New();
        DateTimeOffset now = new(clock.UtcNow, TimeSpan.Zero);
        Result claimed = await ShareNameClaim.ClaimAsync(aliases, command.Name, shareId, now, machine.Name, cancellationToken);
        if (claimed.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(claimed.Error);
        }

        CompressionSetting compression = command.Compress ? CompressionSetting.Gzip : CompressionSetting.None;
        Result<Manifest> manifest = Manifest.CreateNew(shareId, [], now, machine.Name, compression);
        if (manifest.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(manifest.Error);
        }

        Result<string> created = await store.CreateManifestAsync(shareId, manifest.Value, cancellationToken);
        if (created.IsFailure)
        {
            return Result.Failure<InitializeEmptyShareResponse>(created.Error);
        }

        await committer.WriteHistoryAsync(shareId, manifest.Value, cancellationToken);

        Result saved = await stateStore.SaveAsync(
            ShareState.Create(
                location.Value,
                shareId,
                manifest.Value.Revision,
                ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer),
                ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
                []),
            cancellationToken);
        return saved.IsFailure
            ? Result.Failure<InitializeEmptyShareResponse>(saved.Error)
            : new InitializeEmptyShareResponse(shareId, command.Name, compression);
    }
}
