using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Shares;

/// <summary>A share reference resolved: its ID, and the name it was given by, if any.</summary>
public sealed record ResolvedShare(ShareId ShareId, ShareName? Name);

/// <summary>
/// The one way a <see cref="ShareReference"/> becomes a share ID, used by every command that takes
/// one (clone, delete, info, the publishing commands). An ID is used as it is; a name goes through
/// its alias (<see cref="IAliasStore.GetAsync"/>), and a name nobody holds is
/// <see cref="ShareErrors.NameNotFound"/>. Whether the share itself exists is the caller's check.
/// </summary>
internal sealed class ShareResolver(IAliasStore aliases)
{
    public async Task<Result<ResolvedShare>> ResolveAsync(ShareReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Id is { } id)
        {
            return new ResolvedShare(id, null);
        }

        ShareName name = reference.Name!;
        Result<AliasSnapshot?> alias = await aliases.GetAsync(name, cancellationToken);
        if (alias.IsFailure)
        {
            return Result.Failure<ResolvedShare>(alias.Error);
        }

        return alias.Value is { } found
            ? new ResolvedShare(found.Alias.ShareId, name)
            : Result.Failure<ResolvedShare>(ShareErrors.NameNotFound(name));
    }
}

/// <summary>
/// Frees a share's names: every alias pointing at it (more than one after an interrupted rename),
/// each deleted only if it still points there (a fresh read, then <c>If-Match</c>), so a name
/// re-pointed in the meantime is never removed. Used by delete and rename.
/// </summary>
internal sealed class ShareNames(IAliasStore aliases)
{
    public async Task<Result<IReadOnlyList<ShareName>>> FreeAsync(ShareId shareId, ShareName? keep, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<AliasRecord>> found = await aliases.FindByShareAsync(shareId, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<IReadOnlyList<ShareName>>(found.Error);
        }

        List<ShareName> freed = [];
        foreach (AliasRecord alias in found.Value.Where(alias => alias.Name != keep))
        {
            Result<AliasSnapshot?> current = await aliases.GetAsync(alias.Name, cancellationToken);
            if (current.IsFailure)
            {
                return Result.Failure<IReadOnlyList<ShareName>>(current.Error);
            }

            if (current.Value is not { } snapshot || snapshot.Alias.ShareId != shareId)
            {
                continue; // already gone, or now another share's name
            }

            Result deleted = await aliases.DeleteAsync(alias.Name, snapshot.ETag, cancellationToken);
            if (deleted.IsSuccess)
            {
                freed.Add(alias.Name);
            }
            else if (deleted.Error.Code != "Storage.PreconditionFailed")
            {
                return Result.Failure<IReadOnlyList<ShareName>>(deleted.Error);
            }
        }

        return freed;
    }

    public async Task<Result<IReadOnlyList<ShareName>>> OfAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<AliasRecord>> found = await aliases.FindByShareAsync(shareId, cancellationToken);
        return found.IsSuccess
            ? Result.Success<IReadOnlyList<ShareName>>([.. found.Value.Select(alias => alias.Name).Order(Comparer<ShareName>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value)))])
            : Result.Failure<IReadOnlyList<ShareName>>(found.Error);
    }
}
