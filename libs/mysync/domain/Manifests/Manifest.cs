using System.Collections.Immutable;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Domain.Manifests;

/// <summary>
/// The state of a share at one revision (CLAUDE.md §6): <c>manifest.json</c> as a domain object.
/// The JSON shape belongs to Infrastructure's <c>ManifestSerializer</c>; this type holds the rules.
/// </summary>
/// <remarks>
/// Invariants, enforced by every factory:
/// <list type="bullet">
/// <item>each path appears once (after NFC normalization, which <see cref="SharePath"/> applies);</item>
/// <item>nothing is stored "inside" a file (<c>a</c> as a file and <c>a/b</c> can't both exist on disk);</item>
/// <item>directory entries exist only for empty directories; redundant ones are dropped;</item>
/// <item>a deleted share's manifest (<see cref="IsDeleted"/>) has no entries, and nothing follows it:
/// <see cref="CreateNextRevision"/> refuses.</item>
/// </list>
/// </remarks>
public sealed class Manifest
{
    /// <summary>The format of an ordinary manifest. Every client since 0.1 reads it, so it stays 1.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// The format of a deleted share's manifest. A higher number on purpose: a client from before
    /// <c>mysync delete</c> refuses it ("upgrade mysync") instead of reading "no entries" as "every
    /// file was deleted remotely" and deleting its local copies.
    /// </summary>
    public const int DeletedFormatVersion = 2;

    /// <summary>The highest format this build reads.</summary>
    public const int MaxReadableFormatVersion = DeletedFormatVersion;

    private Manifest(
        ShareId shareId,
        Revision revision,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        string updatedBy,
        ImmutableSortedDictionary<SharePath, ManifestEntry> entries,
        ShareDeletion? deletion = null)
    {
        Deletion = deletion;
        ShareId = shareId;
        Revision = revision;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        UpdatedBy = updatedBy;
        Entries = entries;
        FileCount = entries.Values.Count(entry => entry is FileEntry);
        TotalSize = entries.Values.OfType<FileEntry>().Sum(file => file.Size);
    }

    public ShareId ShareId { get; }

    public Revision Revision { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    /// <summary>Who committed this revision, e.g. <c>usman@DEV-LAPTOP</c>.</summary>
    public string UpdatedBy { get; }

    /// <summary>Sorted ordinally by path, so serialization is deterministic.</summary>
    public ImmutableSortedDictionary<SharePath, ManifestEntry> Entries { get; }

    public int FileCount { get; }

    public long TotalSize { get; }

    /// <summary>Set when the share was deleted (<c>mysync delete</c>): who and when. Its objects are gone or going.</summary>
    public ShareDeletion? Deletion { get; }

    public bool IsDeleted => Deletion is not null;

    /// <summary>The format this manifest is written in: <see cref="DeletedFormatVersion"/> for a deleted share, else <see cref="CurrentFormatVersion"/>.</summary>
    public int FormatVersion => IsDeleted ? DeletedFormatVersion : CurrentFormatVersion;

    /// <summary>Revision 1 of a new share.</summary>
    public static Result<Manifest> CreateNew(
        ShareId shareId,
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries,
        DateTimeOffset nowUtc,
        string updatedBy)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        DateTimeOffset now = nowUtc.ToUniversalTime();

        return Build(shareId, Revision.Initial, now, now, updatedBy, entries);
    }

    /// <summary>
    /// The manifest for the next commit: same share, same creation time, revision + 1. This is
    /// the only way to get a higher revision, so "exactly +1 per commit" holds by construction.
    /// </summary>
    public Result<Manifest> CreateNextRevision(
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries,
        DateTimeOffset nowUtc,
        string updatedBy) =>
        Deletion is { } deletion
            ? Result.Failure<Manifest>(ShareErrors.Deleted(ShareId, deletion))
            : Build(ShareId, Revision.Next(), CreatedUtc, nowUtc.ToUniversalTime(), updatedBy, entries);

    /// <summary>
    /// The deleted marker that replaces HEAD when the share is deleted: the next revision, no
    /// entries, and who deleted it when. Deleting an already deleted share is refused.
    /// </summary>
    public Result<Manifest> CreateDeletedRevision(DateTimeOffset nowUtc, string deletedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deletedBy);

        if (Deletion is { } deletion)
        {
            return Result.Failure<Manifest>(ShareErrors.Deleted(ShareId, deletion));
        }

        DateTimeOffset now = nowUtc.ToUniversalTime();
        return new Manifest(
            ShareId,
            Revision.Next(),
            CreatedUtc,
            now,
            deletedBy,
            ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer),
            new ShareDeletion(now, deletedBy));
    }

    /// <summary>
    /// Rebuilds a manifest read from storage. Refuses a format newer than this build supports
    /// (<see cref="ShareErrors.UnsupportedFormat"/>: the user must upgrade, not guess).
    /// </summary>
    /// <remarks>
    /// Timestamps are not cross-checked: they come from different machines' clocks, so
    /// <c>updatedUtc</c> before <c>createdUtc</c> is possible and harmless.
    /// </remarks>
    public static Result<Manifest> Restore(
        int formatVersion,
        ShareId shareId,
        Revision revision,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        string updatedBy,
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries,
        ShareDeletion? deletion = null)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(entries);

        if (formatVersion > MaxReadableFormatVersion)
        {
            return Result.Failure<Manifest>(ShareErrors.UnsupportedFormat(formatVersion));
        }

        if (formatVersion < 1)
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest($"format version {formatVersion} is not valid."));
        }

        if (formatVersion == DeletedFormatVersion != deletion is not null)
        {
            return Result.Failure<Manifest>(ShareErrors.InvalidManifest(
                $"format version {formatVersion} {(deletion is null ? "must say who deleted the share" : "can't record a deletion")}."));
        }

        if (deletion is not null)
        {
            if (entries.Any())
            {
                return Result.Failure<Manifest>(ShareErrors.InvalidManifest("a deleted share's manifest can't list entries."));
            }

            return new Manifest(
                shareId,
                revision,
                createdUtc.ToUniversalTime(),
                updatedUtc.ToUniversalTime(),
                updatedBy,
                ImmutableSortedDictionary.Create<SharePath, ManifestEntry>(SharePath.OrdinalComparer),
                deletion with { DeletedUtc = deletion.DeletedUtc.ToUniversalTime() });
        }

        return Build(shareId, revision, createdUtc.ToUniversalTime(), updatedUtc.ToUniversalTime(), updatedBy, entries);
    }

    private static Result<Manifest> Build(
        ShareId shareId,
        Revision revision,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        string updatedBy,
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(updatedBy);
        ArgumentNullException.ThrowIfNull(entries);

        Result<ImmutableSortedDictionary<SharePath, ManifestEntry>> checkedEntries = CheckEntries(entries);

        return checkedEntries.IsFailure
            ? Result.Failure<Manifest>(checkedEntries.Error)
            : new Manifest(shareId, revision, createdUtc, updatedUtc, updatedBy, checkedEntries.Value);
    }

    private static Result<ImmutableSortedDictionary<SharePath, ManifestEntry>> CheckEntries(
        IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries)
    {
        ImmutableSortedDictionary<SharePath, ManifestEntry>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<SharePath, ManifestEntry>(SharePath.OrdinalComparer);

        foreach ((SharePath path, ManifestEntry entry) in entries)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(entry);

            if (!builder.TryAdd(path, entry))
            {
                return Result.Failure<ImmutableSortedDictionary<SharePath, ManifestEntry>>(
                    ShareErrors.InvalidManifest($"'{PathErrors.Display(path.Value)}' appears more than once."));
            }
        }

        HashSet<SharePath> redundantDirectories = [];

        foreach (SharePath path in builder.Keys)
        {
            for (SharePath? ancestor = path.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (!builder.TryGetValue(ancestor, out ManifestEntry? ancestorEntry))
                {
                    continue;
                }

                if (ancestorEntry is FileEntry)
                {
                    return Result.Failure<ImmutableSortedDictionary<SharePath, ManifestEntry>>(ShareErrors.InvalidManifest(
                        $"'{PathErrors.Display(path.Value)}' is inside '{PathErrors.Display(ancestor.Value)}', which is a file."));
                }

                redundantDirectories.Add(ancestor);
            }
        }

        builder.RemoveRange(redundantDirectories);

        return builder.ToImmutable();
    }
}
