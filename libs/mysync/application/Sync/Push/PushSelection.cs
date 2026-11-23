using System.Collections.Immutable;
using System.Globalization;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;

namespace MySync.Application.Sync.Push;

/// <summary>
/// The filters of one push applied to its listing (CLAUDE.md §10b), before any file is opened: the
/// one place push decides which listed files it takes. Patterns go through the ordinary ignore
/// matcher (<see cref="IIgnoreRulesLoader.Compile"/>), matched against each file's path relative
/// to the target folder. With any filter given, empty folders aren't recorded.
/// </summary>
internal sealed class PushSelection
{
    private readonly PushCommand _command;
    private readonly IIgnoreRulesLoader _rules;

    private PushSelection(PushCommand command, IIgnoreRulesLoader rules, PushFilter filter, PushFilterSettings? settings)
    {
        _command = command;
        _rules = rules;
        Filter = filter;
        Settings = settings;
    }

    public PushFilter Filter { get; }

    /// <summary>What the report and the record show; <c>null</c> when no filter was given.</summary>
    public PushFilterSettings? Settings { get; }

    /// <param name="cutoffUtc">The resolved <c>--updated-since</c> cutoff, if any.</param>
    public static PushSelection Create(PushCommand command, DateTimeOffset? cutoffUtc, IIgnoreRulesLoader rules)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rules);

        if (!command.HasFilters)
        {
            return new PushSelection(command, rules, PushFilter.None, null);
        }

        IReadOnlyList<string> include = command.Include ?? [];
        IReadOnlyList<string> exclude = command.Exclude ?? [];
        long? maxSize = command.MaxSize is { } size ? ByteSize.Parse(size).Value : null;

        PushFilter filter = new(
            command.UpdatedSince is not null,
            cutoffUtc,
            Matcher(include, rules),
            Matcher(exclude, rules),
            maxSize);

        return new PushSelection(command, rules, filter, new PushFilterSettings(cutoffUtc, command.UpdatedSince, [.. include], [.. exclude], maxSize));
    }

    /// <param name="named">Where each file named as an argument lands: leaving one out gets a warning.</param>
    public Selected Apply(SourceListing listing, IReadOnlySet<SharePath> named)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(named);

        if (!Filter.IsActive)
        {
            return new Selected(listing.Files, listing.EmptyFolders, PushFilteredOut.None, []);
        }

        List<SourceFile> kept = [];
        List<PushFilterReason> reasons = [];
        List<string> warnings = [];

        foreach (SourceFile file in listing.Files)
        {
            if (Filter.Decide(file.Path, file.Size, file.MtimeUtc) is not { } reason)
            {
                kept.Add(file);
                continue;
            }

            reasons.Add(reason);
            if (named.Contains(file.Path))
            {
                warnings.Add($"'{PathErrors.Display(file.Path.Value)}' was named, but {Describe(reason, file)}, so it wasn't pushed.");
            }
        }

        return new Selected([.. kept], [], PushFilteredOut.Count(reasons), warnings);
    }

    private static Func<SharePath, bool>? Matcher(IReadOnlyList<string> patterns, IIgnoreRulesLoader rules)
    {
        if (patterns.Count == 0)
        {
            return null;
        }

        // One rule per line; no pattern can be negated or hold a line break (the validator saw to that), so any match counts.
        IIgnoreMatcher matcher = rules.Compile(string.Join('\n', patterns));
        return path => matcher.IsIgnoredOrInsideIgnored(path, isDirectory: false);
    }

    private string Describe(PushFilterReason reason, SourceFile file) => reason switch
    {
        PushFilterReason.NotChanged => string.Create(
            CultureInfo.InvariantCulture,
            $"--updated-since {_command.UpdatedSince} left it out (modified {file.MtimeUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, before {Filter.CutoffUtc!.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)"),
        PushFilterReason.NotIncluded => $"it matches no {PushPatterns.IncludeOption} pattern ({string.Join(", ", (_command.Include ?? []).Select(Quote))})",
        PushFilterReason.Excluded => $"{PushPatterns.ExcludeOption} {Quote(FirstExcludeMatching(file.Path))} left it out",
        _ => string.Create(CultureInfo.InvariantCulture, $"--max-size {_command.MaxSize} left it out (it is {file.Size} bytes)"),
    };

    // Only for a warning about a named file: which exclude pattern it was.
    private string FirstExcludeMatching(SharePath path) =>
        (_command.Exclude ?? []).FirstOrDefault(pattern => _rules.Compile(pattern).IsIgnoredOrInsideIgnored(path, isDirectory: false)) ?? string.Empty;

    private static string Quote(string pattern) => $"'{PathErrors.Display(pattern)}'";
}

/// <summary>The listing after the filters.</summary>
/// <param name="Warnings">Files named as arguments that a filter left out.</param>
internal sealed record Selected(ImmutableArray<SourceFile> Files, ImmutableArray<SharePath> EmptyFolders, PushFilteredOut FilteredOut, IReadOnlyList<string> Warnings);
