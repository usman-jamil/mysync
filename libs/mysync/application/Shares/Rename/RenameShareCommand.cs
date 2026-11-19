using FluentValidation;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.Rename;

/// <summary>
/// <c>mysync rename &lt;name&gt;</c>, in a share's folder: give the share a new name (or a first one,
/// for a share from before names). Claims the new name first, then frees the old ones. Interrupted
/// in between, the share briefly has two names; both resolve to it, and the next rename or delete
/// cleans up.
/// </summary>
public sealed record RenameShareCommand(ShareName NewName) : ICommand<RenameShareResponse>;

/// <param name="Previous">The names freed (none for a share that had no name).</param>
/// <param name="Unchanged">It already had this name, and only this one.</param>
public sealed record RenameShareResponse(ShareId ShareId, ShareName Name, IReadOnlyList<ShareName> Previous, bool Unchanged);

internal sealed class RenameShareCommandValidator : AbstractValidator<RenameShareCommand>
{
    public RenameShareCommandValidator() => RuleFor(command => command.NewName).NotNull();
}

internal sealed class RenameShareCommandHandler(
    ShareFolder shareFolder,
    IShareStore store,
    IAliasStore aliases,
    ShareNames names,
    IMachineIdentity machine,
    IDateTimeProvider clock)
    : ICommandHandler<RenameShareCommand, RenameShareResponse>
{
    public async Task<Result<RenameShareResponse>> Handle(RenameShareCommand command, CancellationToken cancellationToken)
    {
        Result<ShareState> state = await shareFolder.LoadAsync(cancellationToken);
        if (state.IsFailure)
        {
            return Result.Failure<RenameShareResponse>(state.Error);
        }

        ShareId shareId = state.Value.ShareId;
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<RenameShareResponse>(head.Error);
        }

        if (head.Value is not { Manifest: var manifest })
        {
            return Result.Failure<RenameShareResponse>(ShareErrors.NotFound(shareId));
        }

        if (manifest.Deletion is { } deletion)
        {
            return Result.Failure<RenameShareResponse>(ShareErrors.Deleted(shareId, deletion));
        }

        Result<IReadOnlyList<ShareName>> current = await names.OfAsync(shareId, cancellationToken);
        if (current.IsFailure)
        {
            return Result.Failure<RenameShareResponse>(current.Error);
        }

        bool alreadyNamed = current.Value.Contains(command.NewName);
        if (!alreadyNamed)
        {
            // 1. Claim the new name: the conditional create is the uniqueness check.
            Result<string> claimed = await aliases.CreateAsync(
                new AliasRecord(command.NewName, shareId, new DateTimeOffset(clock.UtcNow, TimeSpan.Zero), machine.Name), cancellationToken);
            if (claimed.IsFailure)
            {
                return Result.Failure<RenameShareResponse>(await TakenAsync(command.NewName, claimed.Error, cancellationToken));
            }
        }

        // 2. Free every other name of this share (each only if it still points here).
        Result<IReadOnlyList<ShareName>> freed = await names.FreeAsync(shareId, keep: command.NewName, cancellationToken);
        if (freed.IsFailure)
        {
            return Result.Failure<RenameShareResponse>(freed.Error);
        }

        return new RenameShareResponse(shareId, command.NewName, freed.Value, Unchanged: alreadyNamed && freed.Value.Count == 0);
    }

    private async Task<Error> TakenAsync(ShareName name, Error failure, CancellationToken cancellationToken)
    {
        if (failure.Code != "Storage.AlreadyExists")
        {
            return failure;
        }

        Result<AliasSnapshot?> holder = await aliases.GetAsync(name, cancellationToken);
        return holder is { IsSuccess: true, Value: { } taken } ? ShareErrors.NameTaken(name, taken.Alias.ShareId) : failure;
    }
}
