using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Shares.List;

/// <summary>
/// <c>mysync shares</c>: every named share, from the alias listing alone (no manifest is read).
/// Shares without a name (from before names) aren't listed.
/// </summary>
public sealed record ListSharesQuery : IQuery<ShareListResponse>;

/// <param name="Shares">Sorted by name, ordinally.</param>
/// <param name="Warnings">Aliases that couldn't be read, and stray blobs under <c>aliases/</c>.</param>
public sealed record ShareListResponse(IReadOnlyList<AliasRecord> Shares, IReadOnlyList<string> Warnings);

internal sealed class ListSharesQueryHandler(IAliasStore aliases) : IQueryHandler<ListSharesQuery, ShareListResponse>
{
    public async Task<Result<ShareListResponse>> Handle(ListSharesQuery query, CancellationToken cancellationToken)
    {
        List<AliasRecord> shares = [];
        List<string> warnings = [];
        await foreach (Result<AliasRecord> alias in aliases.ListAsync(cancellationToken))
        {
            if (alias.IsSuccess)
            {
                shares.Add(alias.Value);
            }
            else if (alias.Error.Code is "Storage.InvalidAlias" or "Storage.StrayAlias")
            {
                warnings.Add(alias.Error.Description);
            }
            else
            {
                return Result.Failure<ShareListResponse>(alias.Error);
            }
        }

        return new ShareListResponse([.. shares.OrderBy(alias => alias.Name.Value, StringComparer.Ordinal)], warnings);
    }
}
