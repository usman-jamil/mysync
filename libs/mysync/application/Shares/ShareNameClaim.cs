using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Application.Shares;

/// <summary>
/// Claiming a new share's name (CLAUDE.md §6a): the conditional create is the uniqueness check.
/// Shared by <c>sync --name</c> and <c>tui --name</c>.
/// </summary>
internal static class ShareNameClaim
{
    /// <summary>Taken: say by which share. If it was freed between the claim and the read, try once more.</summary>
    public static async Task<Result> ClaimAsync(IAliasStore aliases, ShareName name, ShareId shareId, DateTimeOffset nowUtc, string createdBy, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Result<string> created = await aliases.CreateAsync(new AliasRecord(name, shareId, nowUtc, createdBy), cancellationToken);
            if (created.IsSuccess)
            {
                return Result.Success();
            }

            if (created.Error.Code != "Storage.AlreadyExists")
            {
                return Result.Failure(created.Error);
            }

            Result<AliasSnapshot?> holder = await aliases.GetAsync(name, cancellationToken);
            if (holder.IsFailure)
            {
                return Result.Failure(holder.Error);
            }

            if (holder.Value is { } taken)
            {
                return Result.Failure(ShareErrors.NameTaken(name, taken.Alias.ShareId));
            }
        }

        return Result.Failure(StorageErrors.PreconditionFailed(ShareLayout.Alias(name)));
    }
}
