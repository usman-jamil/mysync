using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Domain.State;

/// <summary>
/// The per-folder state kept in <c>.mysynctool</c> (CLAUDE.md §7), as a domain object. The JSON
/// shape belongs to Infrastructure's <c>JsonShareStateStore</c>.
/// </summary>
/// <remarks>
/// <see cref="Base"/> is the last state this folder and the remote agreed on and drives the
/// three-way merge. It uses the same <see cref="ManifestEntry"/> type as a manifest, so after a
/// commit the new base is literally the committed manifest's entries.
/// </remarks>
public sealed class ShareState
{
    public const int CurrentFormatVersion = 1;

    private ShareState(
        StorageLocation location,
        ShareId shareId,
        Revision baseRevision,
        ImmutableSortedDictionary<SharePath, ManifestEntry> baseEntries,
        ImmutableSortedDictionary<SharePath, HashCacheEntry> hashCache,
        ImmutableArray<Conflict> conflicts)
    {
        Location = location;
        ShareId = shareId;
        BaseRevision = baseRevision;
        Base = baseEntries;
        HashCache = hashCache;
        Conflicts = conflicts;
    }

    public StorageLocation Location { get; }

    public ShareId ShareId { get; }

    public Revision BaseRevision { get; }

    public ImmutableSortedDictionary<SharePath, ManifestEntry> Base { get; }

    public ImmutableSortedDictionary<SharePath, HashCacheEntry> HashCache { get; }

    /// <summary>Open conflicts, one per path, sorted by path.</summary>
    public ImmutableArray<Conflict> Conflicts { get; }

    /// <summary>State right after creating or cloning: base = that manifest, nothing cached, no conflicts.</summary>
    public static ShareState FromManifest(StorageLocation location, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(manifest);

        return new ShareState(
            location,
            manifest.ShareId,
            manifest.Revision,
            manifest.Entries,
            ImmutableSortedDictionary.Create<SharePath, HashCacheEntry>(SharePath.OrdinalComparer),
            []);
    }

    /// <summary>The state to save after a sync: the agreed base, the new hash cache and the open conflicts.</summary>
    public static ShareState Create(
        StorageLocation location,
        ShareId shareId,
        Revision baseRevision,
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> baseEntries,
        IEnumerable<KeyValuePair<SharePath, HashCacheEntry>> hashCache,
        IEnumerable<Conflict> conflicts)
    {
        Result<ShareState> state = Restore(CurrentFormatVersion, location, shareId, baseRevision, baseEntries, hashCache, conflicts);

        return state.IsSuccess
            ? state.Value
            : throw new ArgumentException(state.Error.Description, nameof(baseEntries));
    }

    /// <summary>
    /// Rebuilds state read from <c>.mysynctool</c>. A newer format is refused; two conflicts for
    /// the same path mean the file is damaged. Never fall back to "new share" on failure.
    /// </summary>
    public static Result<ShareState> Restore(
        int formatVersion,
        StorageLocation location,
        ShareId shareId,
        Revision baseRevision,
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> baseEntries,
        IEnumerable<KeyValuePair<SharePath, HashCacheEntry>> hashCache,
        IEnumerable<Conflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(shareId);
        ArgumentNullException.ThrowIfNull(baseRevision);

        if (formatVersion > CurrentFormatVersion)
        {
            return Result.Failure<ShareState>(StateErrors.UnsupportedFormat(formatVersion));
        }

        if (formatVersion < 1)
        {
            return Result.Failure<ShareState>(StateErrors.Corrupt($"format version {formatVersion} is not valid"));
        }

        ImmutableSortedDictionary<SharePath, ManifestEntry>.Builder baseBuilder =
            ImmutableSortedDictionary.CreateBuilder<SharePath, ManifestEntry>(SharePath.OrdinalComparer);
        foreach ((SharePath path, ManifestEntry entry) in baseEntries)
        {
            if (!baseBuilder.TryAdd(path, entry))
            {
                return Result.Failure<ShareState>(StateErrors.Corrupt($"base lists '{PathErrors.Display(path.Value)}' twice"));
            }
        }

        // A duplicate cache entry is harmless (it is only an optimisation): the last one wins.
        ImmutableSortedDictionary<SharePath, HashCacheEntry>.Builder cache =
            ImmutableSortedDictionary.CreateBuilder<SharePath, HashCacheEntry>(SharePath.OrdinalComparer);
        foreach ((SharePath path, HashCacheEntry entry) in hashCache)
        {
            cache[path] = entry;
        }

        Conflict[] sortedConflicts = [.. conflicts.OrderBy(conflict => conflict.Path, SharePath.OrdinalComparer)];
        for (int i = 1; i < sortedConflicts.Length; i++)
        {
            if (sortedConflicts[i].Path == sortedConflicts[i - 1].Path)
            {
                return Result.Failure<ShareState>(
                    StateErrors.Corrupt($"two conflicts recorded for '{PathErrors.Display(sortedConflicts[i].Path.Value)}'"));
            }
        }

        return new ShareState(location, shareId, baseRevision, baseBuilder.ToImmutable(), cache.ToImmutable(), [.. sortedConflicts]);
    }

    /// <summary>Fails with <see cref="StateErrors.StorageMismatch"/> if the environment points elsewhere.</summary>
    public Result CheckLocation(StorageLocation current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return Location.IsSameAs(current)
            ? Result.Success()
            : Result.Failure(StateErrors.StorageMismatch(Location, current));
    }
}
