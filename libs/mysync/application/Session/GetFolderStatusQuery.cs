using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares;
using MySync.Application.Sync;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Session;

/// <summary>
/// Every path of the share folder with its sync status (CLAUDE.md §10d): <c>mysync status</c>'s
/// preview (<see cref="SyncPipeline.PreviewAsync"/>), mapped to <see cref="FileStatus"/>. A query:
/// it writes nothing, not even the hash cache.
/// </summary>
/// <param name="HashCache">Hashes the caller kept in memory from an earlier scan; reused when size and mtime are unchanged. Never written.</param>
/// <param name="Progress">Reports the scan (hashing large files can take a while).</param>
public sealed record GetFolderStatusQuery(
    ImmutableSortedDictionary<SharePath, HashCacheEntry>? HashCache = null,
    ISyncProgressReporter? Progress = null) : IQuery<FolderStatus>;

internal sealed class GetFolderStatusQueryHandler(
    ShareFolder shareFolder,
    SyncPipeline pipeline,
    IAliasStore aliases,
    ILocalFolder folder,
    SyncOptions options)
    : IQueryHandler<GetFolderStatusQuery, FolderStatus>
{
    public async Task<Result<FolderStatus>> Handle(GetFolderStatusQuery query, CancellationToken cancellationToken)
    {
        Result<ShareState> loaded = await shareFolder.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<FolderStatus>(loaded.Error);
        }

        ShareState state = loaded.Value;

        // The caller's hashes are at least as new as the state file's: theirs win.
        ImmutableSortedDictionary<SharePath, HashCacheEntry> cache = query.HashCache is { } memory
            ? state.HashCache.SetItems(memory)
            : state.HashCache;

        Result<SyncReport> preview = await pipeline.PreviewAsync(
            new SyncSession(
                state.ShareId,
                state.Location,
                state.Base,
                cache,
                state.Conflicts,
                MergeOptions.Default,
                AllowMassDelete: false,
                DryRun: true,
                query.Progress ?? NullSyncProgressReporter.Instance,
                options.MaxParallelism),
            cancellationToken);
        if (preview.IsFailure)
        {
            return Result.Failure<FolderStatus>(preview.Error);
        }

        SyncReport report = preview.Value;
        List<string> warnings = [.. report.Warnings];

        Result<IReadOnlyList<AliasRecord>> named = await aliases.FindByShareAsync(state.ShareId, cancellationToken);
        IReadOnlyList<ShareName> names = named.IsSuccess
            ? [.. named.Value.Select(alias => alias.Name).OrderBy(name => name.Value, StringComparer.Ordinal)]
            : [];
        if (named.IsFailure)
        {
            warnings.Add($"The share's name couldn't be read: {named.Error.Description}");
        }

        return new FolderStatus(
            state.ShareId,
            names,
            state.Location,
            folder.RootPath,
            report.Revision,
            report.HeadUpdatedBy,
            report.HeadUpdatedUtc,
            report.HeadCompression,
            report.HeadEntryCount,
            FolderStatusMapper.Map(report),
            report.Conflicts,
            [.. warnings],
            report.ScanHashCache ?? cache,
            [.. state.Conflicts.Select(conflict => conflict.Path.Value)]);
    }
}
