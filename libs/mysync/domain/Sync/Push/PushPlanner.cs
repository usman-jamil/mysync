using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Domain.Sync.Push;

/// <summary>One pushed file or empty folder, relative to the target folder.</summary>
public sealed record PushedEntry(SharePath Path, ManifestEntry Entry);

/// <summary>The commit a push makes against one HEAD.</summary>
/// <param name="Folder">The target folder chosen (with its suffix, if the first was taken).</param>
/// <param name="Manifest">HEAD's entries plus the pushed ones under <paramref name="Folder"/>.</param>
public sealed record PushPlan(SharePath Folder, Manifest Manifest);

/// <summary>
/// Plans a push against HEAD (CLAUDE.md §10b). A push only <b>adds</b>: it picks the first target
/// folder HEAD has nothing at or under, and puts every pushed entry below it. It never replaces or
/// removes an entry, so every entry of HEAD is in the result unchanged; the one exception is a
/// <c>dir</c> entry of an empty host folder, which the manifest drops because it is no longer empty.
/// That is why a push can never conflict with anything.
/// </summary>
public static class PushPlanner
{
    public static Result<PushPlan> Plan(Manifest head, IReadOnlyList<PushedEntry> pushed, PushTarget target, DateTimeOffset nowUtc, string updatedBy)
    {
        ArgumentNullException.ThrowIfNull(head);

        Result<SharePath> folder = ChooseFolder(head.Entries, target);
        if (folder.IsFailure)
        {
            return Result.Failure<PushPlan>(folder.Error);
        }

        Result<Manifest> next = head.CreateNextRevision(Combine(head.Entries, pushed, folder.Value), nowUtc, updatedBy);
        return next.IsSuccess ? new PushPlan(folder.Value, next.Value) : Result.Failure<PushPlan>(next.Error);
    }

    /// <summary>
    /// The first candidate folder (<see cref="PushTarget.Candidate"/>) with nothing at or under it.
    /// A file at the host folder's place is <see cref="PushErrors.TargetIsAFile"/>.
    /// </summary>
    public static Result<SharePath> ChooseFolder(IReadOnlyDictionary<SharePath, ManifestEntry> head, PushTarget target)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(target);

        if (head.TryGetValue(target.HostFolder, out ManifestEntry? atHost) && atHost is FileEntry)
        {
            return Result.Failure<SharePath>(PushErrors.TargetIsAFile(target.Host));
        }

        // The names directly in the host folder: a candidate among them is taken.
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (SharePath path in head.Keys.Where(path => path.IsUnder(target.HostFolder)))
        {
            taken.Add(path.Segments[1]);
        }

        int number = 1;
        while (taken.Contains(target.Candidate(number).Name))
        {
            number++;
        }

        return target.Candidate(number);
    }

    /// <summary>The pushed entries placed under <paramref name="folder"/>.</summary>
    public static IEnumerable<KeyValuePair<SharePath, ManifestEntry>> Place(IReadOnlyList<PushedEntry> pushed, SharePath folder)
    {
        ArgumentNullException.ThrowIfNull(pushed);
        ArgumentNullException.ThrowIfNull(folder);

        return pushed.Select(entry => new KeyValuePair<SharePath, ManifestEntry>(Under(folder, entry.Path), entry.Entry));
    }

    /// <summary><paramref name="relative"/> inside <paramref name="folder"/>.</summary>
    public static SharePath Under(SharePath folder, SharePath relative)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(relative);

        return SharePath.Create($"{folder.Value}{SharePath.Separator}{relative.Value}").Value;
    }

    private static ImmutableSortedDictionary<SharePath, ManifestEntry> Combine(
        ImmutableSortedDictionary<SharePath, ManifestEntry> head, IReadOnlyList<PushedEntry> pushed, SharePath folder)
    {
        var entries = head.ToBuilder();
        foreach ((SharePath path, ManifestEntry entry) in Place(pushed, folder))
        {
            // The folder is new, so nothing of HEAD's is ever replaced here.
            entries.Add(path, entry);
        }

        return entries.ToImmutable();
    }
}
