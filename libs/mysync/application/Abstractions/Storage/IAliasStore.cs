using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Abstractions.Storage;

/// <summary>
/// Share names: one blob per name, <c>aliases/&lt;name&gt;</c> in the share container
/// (<c>ShareLayout.Alias</c>), the only record of names. Separate from <see cref="IShareStore"/>:
/// a name isn't part of a share's data and has its own lifecycle. Pinned by
/// <c>AliasStoreContractTests</c> against the fake and each real provider.
/// </summary>
/// <remarks>
/// Uniqueness comes from the blob name: <see cref="CreateAsync"/> is a conditional create
/// (<c>If-None-Match: *</c>), so claiming a name and checking it's free are one step, with no race.
/// </remarks>
public interface IAliasStore
{
    /// <summary>Claims the name: <c>If-None-Match: *</c>; <c>Storage.AlreadyExists</c> when it's taken. Returns the ETag.</summary>
    Task<Result<string>> CreateAsync(AliasRecord alias, CancellationToken cancellationToken);

    /// <summary>The alias with its ETag, or a <c>null</c> value when there's none.</summary>
    Task<Result<AliasSnapshot?>> GetAsync(ShareName name, CancellationToken cancellationToken);

    /// <summary>
    /// Every alias, from one paged listing of <c>aliases/</c> (no per-alias reads). An alias that
    /// can't be read is a failed item naming it (<c>Storage.InvalidAlias</c>), and a blob under
    /// <c>aliases/</c> that isn't a name is one too (<c>Storage.StrayAlias</c>): the listing goes on.
    /// Any other failure ends it.
    /// </summary>
    IAsyncEnumerable<Result<AliasRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every alias of one share (more than one after an interrupted rename), from the same single
    /// listing filtered by share ID. Manifests don't hold names, so this is how delete, rename and
    /// info get from a share to its names. Unreadable aliases are left out.
    /// </summary>
    Task<Result<IReadOnlyList<AliasRecord>>> FindByShareAsync(ShareId shareId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the alias only if it hasn't changed since read (<c>If-Match</c>); otherwise, or when
    /// it's already gone, <c>Storage.PreconditionFailed</c>.
    /// </summary>
    Task<Result> DeleteAsync(ShareName name, string ifMatchETag, CancellationToken cancellationToken);
}

/// <summary>An alias as read, with the ETag a later <see cref="IAliasStore.DeleteAsync"/> must match.</summary>
public sealed record AliasSnapshot(AliasRecord Alias, string ETag);
