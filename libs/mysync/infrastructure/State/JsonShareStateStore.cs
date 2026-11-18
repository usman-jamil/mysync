using System.Text.Json;
using MySync.Application.Abstractions.State;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.Serialization;
using SharedKernel;

namespace MySync.Infrastructure.State;

/// <summary>
/// <c>.mysynctool</c> (CLAUDE.md §7). Written atomically: <c>.mysynctool.new</c> is written and
/// flushed to disk, then renamed over the old file, so a crash leaves either the old state or the
/// new one, never half of one.
/// </summary>
/// <remarks>
/// Reading never guesses. A missing file is "not a share"; anything else that can't be read,
/// parsed or validated is <see cref="StateErrors.Corrupt"/> (or <see cref="StateErrors.UnsupportedFormat"/>),
/// because treating a damaged base as empty would make every delete look like an addition. The
/// one exception is the hash cache, which is only an optimisation: an unusable cache entry is
/// dropped and that file is hashed again.
/// </remarks>
internal sealed class JsonShareStateStore(ShareRoot root) : IShareStateStore
{
    private string StatePath => root.Combine(StateFileNames.State);

    private string TempPath => root.Combine(StateFileNames.StateTemp);

    public async Task<Result<ShareState?>> LoadAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            if (Directory.Exists(StatePath))
            {
                return Result.Failure<ShareState?>(StateErrors.Corrupt("it is a directory"));
            }

            if (!File.Exists(StatePath))
            {
                return Result.Success<ShareState?>(null);
            }

            bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<ShareState?>(StateErrors.Corrupt(exception.Message));
        }

        Result<ShareState> state = Parse(bytes);
        return state.IsSuccess ? Result.Success<ShareState?>(state.Value) : Result.Failure<ShareState?>(state.Error);
    }

    // Synchronous on purpose: there is no asynchronous flush to disk, and the file is small.
    public Task<Result> SaveAsync(ShareState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ToJson(state), MySyncJsonContext.Default.ShareStateJson);

        try
        {
            using (FileStream temp = new(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                temp.Write(bytes);
#pragma warning disable CA1849 // There is no asynchronous flush to disk; the state must reach it before the rename.
                temp.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            File.Move(TempPath, StatePath, overwrite: true);
            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(StateFileNames.State, exception.Message)));
        }
    }

    public Task<Result> DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(TempPath);
            File.Delete(StatePath);
            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(StateFileNames.State, exception.Message)));
        }
    }

    internal static Result<ShareState> Parse(ReadOnlySpan<byte> bytes)
    {
        ShareStateJson? json;
        try
        {
            FormatProbeJson? probe = JsonSerializer.Deserialize(bytes, MySyncJsonContext.Default.FormatProbeJson);
            if (probe is not null && probe.FormatVersion > ShareState.CurrentFormatVersion)
            {
                return Result.Failure<ShareState>(StateErrors.UnsupportedFormat(probe.FormatVersion));
            }

            json = JsonSerializer.Deserialize(bytes, MySyncJsonContext.Default.ShareStateJson);
        }
        catch (JsonException exception)
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt($"not valid JSON: {exception.Message}"));
        }

        if (json is null)
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt("it is empty"));
        }

        if (string.IsNullOrWhiteSpace(json.Provider) || string.IsNullOrWhiteSpace(json.AccountName) || string.IsNullOrWhiteSpace(json.Container))
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt("provider, accountName or container is empty"));
        }

        if (!ShareId.TryParse(json.ShareId, out ShareId? shareId))
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt("shareId is not a UUID"));
        }

        if (!Revision.TryCreate(json.BaseRevision, out Revision? baseRevision))
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt($"baseRevision {json.BaseRevision} is not valid"));
        }

        Result<List<KeyValuePair<SharePath, ManifestEntry>>> baseEntries = EntryMapping.ToDomain(json.Base, Corrupt("base"));
        if (baseEntries.IsFailure)
        {
            return Result.Failure<ShareState>(baseEntries.Error);
        }

        List<Conflict> conflicts = [];
        foreach (ConflictJson conflict in json.Conflicts)
        {
            Result<Conflict> parsed = ToDomain(conflict);
            if (parsed.IsFailure)
            {
                return Result.Failure<ShareState>(parsed.Error);
            }

            conflicts.Add(parsed.Value);
        }

        return ShareState.Restore(
            json.FormatVersion,
            new StorageLocation(json.Provider, json.AccountName, json.Container),
            shareId,
            baseRevision,
            baseEntries.Value,
            HashCache(json.HashCache),
            conflicts);
    }

    internal static ShareStateJson ToJson(ShareState state) => new(
        ShareState.CurrentFormatVersion,
        state.Location.Provider,
        state.Location.AccountName,
        state.Location.Container,
        state.ShareId.ToString(),
        state.BaseRevision.Value,
        EntryMapping.ToJson(state.Base),
        state.HashCache.ToDictionary(
            pair => pair.Key.Value,
            pair => new HashCacheJson(pair.Value.Size, pair.Value.MtimeTicksUtc, pair.Value.Sha256.Value),
            StringComparer.Ordinal),
        [.. state.Conflicts.Select(conflict => new ConflictJson(
            conflict.Path.Value,
            conflict.Kind.ToCode(),
            ToJsonOrNull(conflict.Base),
            ToJsonOrNull(conflict.Local),
            ToJsonOrNull(conflict.Remote),
            conflict.RemoteRevision.Value,
            conflict.RemoteUpdatedBy,
            conflict.FirstDetectedUtc,
            conflict.LastSeenUtc))]);

    private static EntryJson? ToJsonOrNull(ManifestEntry? entry) => entry is null ? null : EntryMapping.ToJson(entry);

    private static Func<string, Error> Corrupt(string section) => detail => StateErrors.Corrupt($"{section}: {detail}");

    private static List<KeyValuePair<SharePath, HashCacheEntry>> HashCache(Dictionary<string, HashCacheJson> cache)
    {
        List<KeyValuePair<SharePath, HashCacheEntry>> entries = new(cache.Count);
        foreach ((string key, HashCacheJson json) in cache)
        {
            if (SharePath.Create(key) is { IsSuccess: true } path && ContentHash.TryParse(json.Sha256, out ContentHash? hash) && json.Size >= 0)
            {
                entries.Add(new KeyValuePair<SharePath, HashCacheEntry>(path.Value, new HashCacheEntry(json.Size, json.MtimeTicksUtc, hash)));
            }
        }

        return entries;
    }

    private static Result<Conflict> ToDomain(ConflictJson json)
    {
        Func<string, Error> corrupt = Corrupt("conflicts");

        Result<SharePath> path = SharePath.Create(json.Path);
        if (path.IsFailure)
        {
            return Result.Failure<Conflict>(corrupt(path.Error.Description));
        }

        if (!ConflictKindCodes.TryParse(json.Kind, out ConflictKind kind))
        {
            return Result.Failure<Conflict>(corrupt($"unknown kind '{PathErrors.Display(json.Kind)}'"));
        }

        if (!Revision.TryCreate(json.RemoteRevision, out Revision? remoteRevision))
        {
            return Result.Failure<Conflict>(corrupt($"remoteRevision {json.RemoteRevision} is not valid"));
        }

        Result<ManifestEntry?> baseEntry = Side(json.Base, "base", corrupt);
        Result<ManifestEntry?> local = Side(json.Local, "local", corrupt);
        Result<ManifestEntry?> remote = Side(json.Remote, "remote", corrupt);
        foreach (Result<ManifestEntry?> side in (Result<ManifestEntry?>[])[baseEntry, local, remote])
        {
            if (side.IsFailure)
            {
                return Result.Failure<Conflict>(side.Error);
            }
        }

        return new Conflict(
            path.Value, kind, baseEntry.Value, local.Value, remote.Value, remoteRevision, json.RemoteUpdatedBy, json.FirstDetectedUtc, json.LastSeenUtc);
    }

    private static Result<ManifestEntry?> Side(EntryJson? json, string side, Func<string, Error> error)
    {
        if (json is null)
        {
            return Result.Success<ManifestEntry?>(null);
        }

        Result<ManifestEntry> entry = EntryMapping.ToDomain(json, side, error);
        return entry.IsSuccess ? Result.Success<ManifestEntry?>(entry.Value) : Result.Failure<ManifestEntry?>(entry.Error);
    }
}
