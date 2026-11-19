using System.Collections.Immutable;
using FluentValidation;
using MySync.Application.Abstractions.Messaging;
using MySync.Domain.Content;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.Sync;

namespace MySync.Application.Conflicts.Diff;

/// <summary>
/// <c>mysync diff</c> (CLAUDE.md §10): what differs between this folder's version of a file and the
/// share's, to decide a conflict. Read-only: no lock, no state, no log, no staging.
/// </summary>
/// <param name="Paths">Share paths as typed (already <c>/</c>-separated); empty = every open conflict.</param>
/// <param name="AgainstBase"><c>--base</c>: for modify/modify, base → local and base → remote instead of local → remote.</param>
/// <param name="StatOnly"><c>--stat</c>: counts only, no hunks.</param>
public sealed record DiffQuery(
    IReadOnlyList<string> Paths,
    int Context = DiffLimits.DefaultContext,
    bool AgainstBase = false,
    bool StatOnly = false) : IQuery<DiffResponse>;

public sealed record DiffResponse(ShareId ShareId, ImmutableArray<PathDiff> Items);

public enum DiffSideKind
{
    Local,
    Remote,
    Base,
}

/// <param name="Kind"><c>null</c>: not a conflict, compared with the share's current HEAD.</param>
/// <param name="Local">The file on disk now; <c>null</c> when there is none.</param>
/// <param name="Remote">The conflict's recorded remote version, or HEAD's entry; <c>null</c> when absent.</param>
/// <param name="Base">The last agreed version (conflicts only); <c>null</c> when absent.</param>
public sealed record PathDiff(
    SharePath Path,
    ConflictKind? Kind,
    DiffSide? Local,
    DiffSide? Remote,
    DiffSide? Base,
    ImmutableArray<DiffSection> Sections,
    ImmutableArray<string> Notes);

/// <param name="Revision">Remote side only: the HEAD revision it was read from (for a conflict, the one it was recorded against).</param>
/// <param name="UpdatedBy">Remote side only: who committed that revision.</param>
public sealed record DiffSide(
    EntryType Type,
    ContentHash? Sha256,
    long? Size,
    DateTimeOffset? MtimeUtc,
    bool Executable,
    Revision? Revision = null,
    string? UpdatedBy = null);

/// <param name="OldLabel"><c>local/&lt;path&gt;</c>, <c>remote/…</c>, <c>base/…</c>, or <c>/dev/null</c> for an absent side (so the patch applies).</param>
public sealed record DiffSection(string Title, DiffSideKind OldSide, DiffSideKind NewSide, string OldLabel, string NewLabel, DiffOutcome Outcome);

/// <summary>What a section found. One of the sealed records below.</summary>
public abstract record DiffOutcome;

public sealed record IdenticalOutcome : DiffOutcome;

/// <param name="Hunks">Empty with <c>--stat</c>.</param>
public sealed record TextOutcome(ImmutableArray<DiffHunk> Hunks, int Added, int Removed) : DiffOutcome;

public sealed record BinaryOutcome : DiffOutcome;

public sealed record TooLargeOutcome(long? OldSize, long? NewSize) : DiffOutcome;

public sealed record EolOnlyOutcome(LineEndingStyle Old, LineEndingStyle New) : DiffOutcome;

public sealed record ExecOnlyOutcome(bool OldExecutable, bool NewExecutable) : DiffOutcome;

public sealed record TypeMismatchOutcome(string Description) : DiffOutcome;

internal sealed class DiffQueryValidator : AbstractValidator<DiffQuery>
{
    public DiffQueryValidator()
    {
        RuleFor(query => query.Context)
            .InclusiveBetween(DiffLimits.MinContext, DiffLimits.MaxContext)
            .WithErrorCode("Diff.InvalidContext")
            .WithMessage($"--context must be between {DiffLimits.MinContext} and {DiffLimits.MaxContext}.");
    }
}
