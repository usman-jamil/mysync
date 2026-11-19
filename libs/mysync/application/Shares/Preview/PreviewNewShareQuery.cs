using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.State;
using MySync.Domain.Shares;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Application.Shares.Preview;

/// <summary>
/// What <c>mysync sync</c> without an ID would do here, before doing it: is this already a share,
/// and if not, how big would the new one be (counts only, no hashing). The command uses it to
/// pick the flow and to decide whether to ask for confirmation (CLAUDE.md §9).
/// </summary>
/// <param name="CountFiles">
/// False to only answer "is this already a share?" (no walk of the folder): the command asks that
/// first when a new share couldn't be created anyway (no <c>--name</c>), so it fails at once.
/// </param>
public sealed record PreviewNewShareQuery(bool CountFiles = true) : IQuery<NewSharePreview>;

/// <param name="ExistingShareId">Set if the folder already has a <c>.mysynctool</c>: run a sync instead.</param>
/// <param name="IsUnsafeRoot">Home directory or a drive root: creating needs <c>--force</c>.</param>
public sealed record NewSharePreview(ShareId? ExistingShareId, string RootPath, bool IsUnsafeRoot, int FileCount, long TotalBytes);

internal sealed class PreviewNewShareQueryHandler(
    IShareStateStore stateStore,
    ILocalFolder folder,
    IFileScanner scanner,
    IIgnoreRulesLoader ignoreRules)
    : IQueryHandler<PreviewNewShareQuery, NewSharePreview>
{
    public async Task<Result<NewSharePreview>> Handle(PreviewNewShareQuery query, CancellationToken cancellationToken)
    {
        // A damaged .mysynctool is an error here too: it must never look like "not a share yet".
        Result<ShareState?> state = await stateStore.LoadAsync(cancellationToken);
        if (state.IsFailure)
        {
            return Result.Failure<NewSharePreview>(state.Error);
        }

        if (state.Value is { } existing)
        {
            return new NewSharePreview(existing.ShareId, folder.RootPath, folder.IsUnsafeRoot, 0, 0);
        }

        if (!query.CountFiles)
        {
            return new NewSharePreview(null, folder.RootPath, folder.IsUnsafeRoot, 0, 0);
        }

        Result<IIgnoreMatcher> ignore = await ignoreRules.LoadAsync(cancellationToken);
        if (ignore.IsFailure)
        {
            return Result.Failure<NewSharePreview>(ignore.Error);
        }

        Result<FolderSummary> summary = await scanner.SummarizeAsync(ignore.Value, cancellationToken);
        if (summary.IsFailure)
        {
            return Result.Failure<NewSharePreview>(summary.Error);
        }

        return new NewSharePreview(null, folder.RootPath, folder.IsUnsafeRoot, summary.Value.FileCount, summary.Value.TotalBytes);
    }
}
