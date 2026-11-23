using MySync.Domain.Paths;

namespace MySync.Domain.Sync.Push;

/// <summary>Why a filter left a file out of a push. Also the order the filters are asked in.</summary>
public enum PushFilterReason
{
    /// <summary>Modified before the <c>--updated-since</c> cutoff.</summary>
    NotChanged,

    /// <summary>Matches no <c>--include</c> pattern.</summary>
    NotIncluded,

    /// <summary>Matches an <c>--exclude</c> pattern.</summary>
    Excluded,

    /// <summary>Larger than <c>--max-size</c>.</summary>
    TooLarge,
}

/// <summary>
/// Which files a push takes (CLAUDE.md §10b), decided on the listing before anything is opened. A
/// file is kept only if every filter given keeps it; one left out is counted once, under the first
/// filter that left it out, in the order of <see cref="PushFilterReason"/>. Exclude wins over
/// include. The patterns come in as predicates, so the domain knows nothing of the matcher.
/// </summary>
public sealed class PushFilter
{
    private readonly Func<SharePath, bool>? _include;
    private readonly Func<SharePath, bool>? _exclude;

    /// <param name="updatedSinceGiven"><c>--updated-since</c> was given, even if it resolved to no cutoff (no earlier push).</param>
    /// <param name="cutoffUtc">Files modified before this are left out; <c>null</c> for no cutoff.</param>
    /// <param name="include">Whether a path matches an <c>--include</c> pattern; <c>null</c> when none was given.</param>
    /// <param name="exclude">Whether a path matches an <c>--exclude</c> pattern; <c>null</c> when none was given.</param>
    /// <param name="maxSizeBytes">Files larger than this are left out; <c>null</c> for no limit.</param>
    public PushFilter(bool updatedSinceGiven, DateTimeOffset? cutoffUtc, Func<SharePath, bool>? include, Func<SharePath, bool>? exclude, long? maxSizeBytes)
    {
        CutoffUtc = cutoffUtc;
        _include = include;
        _exclude = exclude;
        MaxSizeBytes = maxSizeBytes;
        IsActive = updatedSinceGiven || cutoffUtc is not null || include is not null || exclude is not null || maxSizeBytes is not null;
    }

    /// <summary>No filter: every file is kept.</summary>
    public static PushFilter None { get; } = new(updatedSinceGiven: false, null, null, null, null);

    /// <summary>Any filter option was given. Then empty folders aren't recorded.</summary>
    public bool IsActive { get; }

    public DateTimeOffset? CutoffUtc { get; }

    public long? MaxSizeBytes { get; }

    /// <param name="path">The file's path relative to the target folder.</param>
    /// <returns><c>null</c> to keep it, otherwise the first reason to leave it out.</returns>
    public PushFilterReason? Decide(SharePath path, long size, DateTimeOffset mtimeUtc)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (CutoffUtc is { } cutoff && mtimeUtc < cutoff)
        {
            return PushFilterReason.NotChanged;
        }

        if (_include is not null && !_include(path))
        {
            return PushFilterReason.NotIncluded;
        }

        if (_exclude is not null && _exclude(path))
        {
            return PushFilterReason.Excluded;
        }

        return MaxSizeBytes is { } limit && size > limit ? PushFilterReason.TooLarge : null;
    }
}

/// <summary>How many files the filters left out, by the first filter that did.</summary>
public sealed record PushFilteredOut(int NotChanged, int NotIncluded, int Excluded, int TooLarge)
{
    public static PushFilteredOut None { get; } = new(0, 0, 0, 0);

    public int Total => NotChanged + NotIncluded + Excluded + TooLarge;

    public static PushFilteredOut Count(IEnumerable<PushFilterReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        int[] counts = new int[4];
        foreach (PushFilterReason reason in reasons)
        {
            counts[(int)reason]++;
        }

        return new PushFilteredOut(counts[0], counts[1], counts[2], counts[3]);
    }
}

/// <summary>The filters a push was given, as the report and the push record show them.</summary>
/// <param name="UpdatedSinceUtc">The resolved cutoff, or <c>null</c> (none given, or no earlier push for <c>last</c>).</param>
/// <param name="UpdatedSinceSpec"><c>--updated-since</c> as typed, or <c>null</c>.</param>
public sealed record PushFilterSettings(
    DateTimeOffset? UpdatedSinceUtc,
    string? UpdatedSinceSpec,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    long? MaxSizeBytes);

/// <summary>
/// What an <c>--include</c> / <c>--exclude</c> pattern may be: one gitignore pattern, as in
/// <c>.mysyncignore</c>, but never a negation (exclude and include already say which way), never
/// empty, never a comment, and on one line (a second line would be a second rule).
/// </summary>
public static class PushPatterns
{
    public const string IncludeOption = "--include";
    public const string ExcludeOption = "--exclude";

    public static SharedKernel.Result Check(string option, string pattern)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(pattern);

        string? reason = pattern switch
        {
            _ when string.IsNullOrWhiteSpace(pattern) => "it is empty.",
            _ when pattern.StartsWith('!') => $"negated patterns aren't supported; use {IncludeOption} or {ExcludeOption} instead.",
            _ when pattern.StartsWith('#') => "it would be read as a comment; write \\# for a name that starts with #.",
            _ when pattern.Any(char.IsControl) => "it holds a line break or another control character.",
            _ => null,
        };

        return reason is null ? SharedKernel.Result.Success() : SharedKernel.Result.Failure(PushErrors.InvalidPattern(option, pattern, reason));
    }
}
