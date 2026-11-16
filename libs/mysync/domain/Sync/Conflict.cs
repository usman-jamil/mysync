using MySync.Domain.Manifests;
using MySync.Domain.Paths;

namespace MySync.Domain.Sync;

public enum ConflictKind
{
    ModifyModify = 0,
    AddAdd = 1,
    ModifyDelete = 2,
    DeleteModify = 3,
    Type = 4,
}

/// <summary>
/// An open conflict (CLAUDE.md §9): both sides changed <see cref="Path"/> differently since the
/// base. Nothing was done to it on either side. <c>null</c> entries mean "absent on that side".
/// Which kind applies is decided by the merge planner (step 3); this is the recorded fact.
/// </summary>
public sealed record Conflict(
    SharePath Path,
    ConflictKind Kind,
    ManifestEntry? Base,
    ManifestEntry? Local,
    ManifestEntry? Remote,
    Revision RemoteRevision,
    string RemoteUpdatedBy,
    DateTimeOffset FirstDetectedUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>The text form used in <c>.mysynctool</c>, <c>.mysynctool.log</c>, <c>--json</c> and on screen.</summary>
public static class ConflictKindCodes
{
    public static string ToCode(this ConflictKind kind) => kind switch
    {
        ConflictKind.ModifyModify => "modify/modify",
        ConflictKind.AddAdd => "add/add",
        ConflictKind.ModifyDelete => "modify/delete",
        ConflictKind.DeleteModify => "delete/modify",
        ConflictKind.Type => "type",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParse(string? code, out ConflictKind kind)
    {
        foreach (ConflictKind candidate in Enum.GetValues<ConflictKind>())
        {
            if (string.Equals(candidate.ToCode(), code, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
