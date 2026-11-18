using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Publishing;

/// <summary>
/// Takes a publication down, in the order that kills the link first: its <c>index.html</c>, then
/// everything else under <c>p/&lt;publishId&gt;/</c>, then its record. Shared by <c>unpublish</c>
/// and <c>delete</c>. Re-running finishes an interrupted removal.
/// </summary>
internal sealed class PublicationRemover(IPublishTarget target, PublicationRegistryUpdater registry)
{
    /// <summary>Blobs deleted (index.html included).</summary>
    public async Task<Result<int>> RemoveAsync(ShareId shareId, Publication publication, PublishLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(location);

        if (!string.Equals(publication.PublishAccount, location.AccountName, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<int>(PublishErrors.OtherAccount(publication.Id, publication.PublishAccount, location.AccountName));
        }

        var layout = PublicationLayout.For(publication.Id);
        Result index = await target.DeleteAsync(layout.Index, cancellationToken);
        if (index.IsFailure)
        {
            return Result.Failure<int>(index.Error);
        }

        Result<int> rest = await target.DeletePrefixAsync(layout.Prefix, cancellationToken);
        if (rest.IsFailure)
        {
            return rest;
        }

        Result<Domain.Publishing.PublicationRegistry> updated = await registry.UpdateAsync(shareId, current => current.Without(publication.Id), cancellationToken);
        return updated.IsSuccess ? rest.Value + 1 : Result.Failure<int>(updated.Error);
    }
}
