using System.Collections.Immutable;
using MySync.Application.Abstractions.Messaging;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Publishing.List;

/// <summary><c>mysync publications [--share &lt;id&gt;]</c>: the share's publication records. Needs the private account only.</summary>
public sealed record ListPublicationsQuery(ShareReference? Share = null) : IQuery<PublicationListResponse>;

public sealed record PublicationListResponse(ShareId ShareId, ImmutableArray<Publication> Publications);

internal sealed class ListPublicationsQueryHandler(PublishingShare share, PublicationRegistryUpdater registry)
    : IQueryHandler<ListPublicationsQuery, PublicationListResponse>
{
    public async Task<Result<PublicationListResponse>> Handle(ListPublicationsQuery query, CancellationToken cancellationToken)
    {
        Result<PublishingTarget> resolved = await share.ResolveAsync(query.Share, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<PublicationListResponse>(resolved.Error);
        }

        Result<PublicationRegistry> read = await registry.ReadAsync(resolved.Value.ShareId, cancellationToken);
        return read.IsSuccess
            ? new PublicationListResponse(resolved.Value.ShareId, [.. read.Value.Publications])
            : Result.Failure<PublicationListResponse>(read.Error);
    }
}
