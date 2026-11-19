using System.Collections.Immutable;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Shares;
using MySync.Domain.Diffing;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Conflicts.Diff;

internal sealed class DiffQueryHandler(ShareFolder shareFolder, DiffSources sources) : IQueryHandler<DiffQuery, DiffResponse>
{
    internal const string LocalChangedNote = "Your file changed since the conflict was recorded; this is what --keep local would upload.";
    internal const string RemoteChangedNote = "The share changed this file again since the conflict was recorded; run mysync sync to refresh it.";
    internal const string ShareDeletedNote = "The share deleted this file.";
    internal const string YouDeletedNote = "You deleted this file.";

    public async Task<Result<DiffResponse>> Handle(DiffQuery query, CancellationToken cancellationToken)
    {
        Result<ShareState> loaded = await shareFolder.LoadAsync(cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<DiffResponse>(loaded.Error);
        }

        ShareState state = loaded.Value;
        var conflicts = state.Conflicts.ToDictionary(conflict => conflict.Path);

        List<SharePath> paths = [];
        if (query.Paths.Count == 0)
        {
            paths.AddRange(state.Conflicts.Select(conflict => conflict.Path).Order(SharePath.OrdinalComparer));
        }
        else
        {
            foreach (string typed in query.Paths)
            {
                Result<SharePath> path = SharePath.Create(typed);
                if (path.IsFailure)
                {
                    return Result.Failure<DiffResponse>(path.Error);
                }

                if (!paths.Contains(path.Value))
                {
                    paths.Add(path.Value);
                }
            }
        }

        HeadReader head = new(sources, state.ShareId);
        ImmutableArray<PathDiff>.Builder items = ImmutableArray.CreateBuilder<PathDiff>(paths.Count);
        foreach (SharePath path in paths)
        {
            Result<PathDiff> item = conflicts.TryGetValue(path, out Conflict? conflict)
                ? await ForConflictAsync(state, conflict, head, query, cancellationToken)
                : await AgainstHeadAsync(state, path, head, query, cancellationToken);
            if (item.IsFailure)
            {
                return Result.Failure<DiffResponse>(item.Error);
            }

            items.Add(item.Value);
        }

        return new DiffResponse(state.ShareId, items.ToImmutable());
    }

    private async Task<Result<PathDiff>> ForConflictAsync(ShareState state, Conflict conflict, HeadReader head, DiffQuery query, CancellationToken cancellationToken)
    {
        SharePath path = conflict.Path;
        Result<Side> local = await sources.LocalAsync(path, Recorded(conflict.Local) ?? Recorded(conflict.Base), cancellationToken);
        if (local.IsFailure)
        {
            return Result.Failure<PathDiff>(local.Error);
        }

        Side remote = sources.Stored(state.ShareId, path, DiffSideKind.Remote, conflict.Remote, conflict.RemoteRevision, conflict.RemoteUpdatedBy);
        Side @base = sources.Stored(state.ShareId, path, DiffSideKind.Base, conflict.Base);

        List<string> notes = [];
        if (!local.Value.Matches(conflict.Local))
        {
            notes.Add(LocalChangedNote);
        }

        Result<Manifest> manifest = await head.ReadAsync(cancellationToken);
        if (manifest.IsFailure)
        {
            return Result.Failure<PathDiff>(manifest.Error);
        }

        if (!ManifestEntry.AreEquivalent(manifest.Value.Entries.GetValueOrDefault(path), conflict.Remote))
        {
            notes.Add(RemoteChangedNote);
        }

        List<(string Title, Side Old, Side New)> pairs = conflict.Kind switch
        {
            ConflictKind.ModifyModify when query.AgainstBase && conflict.Base is FileEntry =>
                [("your changes", @base, local.Value), ("their changes", @base, remote)],
            ConflictKind.ModifyDelete => [("your changes", @base, local.Value)],
            ConflictKind.DeleteModify => [("their changes", @base, remote)],
            _ => [("local vs remote", local.Value, remote)],
        };

        if (conflict.Kind == ConflictKind.ModifyDelete)
        {
            notes.Add(ShareDeletedNote);
        }
        else if (conflict.Kind == ConflictKind.DeleteModify)
        {
            notes.Add(YouDeletedNote);
        }

        ImmutableArray<DiffSection>.Builder sections = ImmutableArray.CreateBuilder<DiffSection>();
        if (conflict.Kind == ConflictKind.Type)
        {
            sections.Add(Section("local vs remote", local.Value, remote, path, new TypeMismatchOutcome($"{Describe(conflict.Local)} here, {Describe(conflict.Remote)} in the share")));
        }
        else
        {
            foreach ((string title, Side old, Side @new) in pairs)
            {
                Result<DiffSection> section = await CompareAsync(title, old, @new, path, query, notes, cancellationToken);
                if (section.IsFailure)
                {
                    return Result.Failure<PathDiff>(section.Error);
                }

                sections.Add(section.Value);
            }
        }

        return new PathDiff(path, conflict.Kind, local.Value.Info, remote.Info, @base.Info, sections.ToImmutable(), [.. notes.Distinct(StringComparer.Ordinal)]);
    }

    private async Task<Result<PathDiff>> AgainstHeadAsync(ShareState state, SharePath path, HeadReader head, DiffQuery query, CancellationToken cancellationToken)
    {
        Result<Manifest> manifest = await head.ReadAsync(cancellationToken);
        if (manifest.IsFailure)
        {
            return Result.Failure<PathDiff>(manifest.Error);
        }

        ManifestEntry? headEntry = manifest.Value.Entries.GetValueOrDefault(path);
        Result<Side> local = await sources.LocalAsync(path, Recorded(state.Base.GetValueOrDefault(path)), cancellationToken);
        if (local.IsFailure)
        {
            return Result.Failure<PathDiff>(local.Error);
        }

        if (local.Value.Entry is null && headEntry is null)
        {
            return Result.Failure<PathDiff>(DiffErrors.PathNotFound(path.Value));
        }

        Side remote = sources.Stored(state.ShareId, path, DiffSideKind.Remote, headEntry, manifest.Value.Revision, manifest.Value.UpdatedBy);
        List<string> notes = [];
        DiffSection section;
        if (headEntry is DirectoryEntry)
        {
            section = Section("local vs remote", local.Value, remote, path, new TypeMismatchOutcome($"{Describe(local.Value.Entry)} here, {Describe(headEntry)} in the share"));
        }
        else
        {
            Result<DiffSection> compared = await CompareAsync("local vs remote", local.Value, remote, path, query, notes, cancellationToken);
            if (compared.IsFailure)
            {
                return Result.Failure<PathDiff>(compared.Error);
            }

            section = compared.Value;
        }

        return new PathDiff(path, null, local.Value.Info, remote.Info, null, [section], [.. notes]);
    }

    private static async Task<Result<DiffSection>> CompareAsync(
        string title, Side old, Side @new, SharePath path, DiffQuery query, List<string> notes, CancellationToken cancellationToken)
    {
        DiffOutcome? quick = (old.Entry, @new.Entry) switch
        {
            (null, null) => new IdenticalOutcome(),
            (FileEntry a, FileEntry b) when a.Sha256 == b.Sha256 =>
                a.Executable == b.Executable ? new IdenticalOutcome() : new ExecOnlyOutcome(a.Executable, b.Executable),
            _ => null,
        };
        if (quick is not null)
        {
            return Section(title, old, @new, path, quick);
        }

        Result<byte[]?> oldBytes = await old.ReadAsync(cancellationToken);
        if (oldBytes.IsFailure)
        {
            return Result.Failure<DiffSection>(oldBytes.Error);
        }

        Result<byte[]?> newBytes = await @new.ReadAsync(cancellationToken);
        if (newBytes.IsFailure)
        {
            return Result.Failure<DiffSection>(newBytes.Error);
        }

        if (oldBytes.Value is null || newBytes.Value is null)
        {
            return Section(title, old, @new, path, new TooLargeOutcome(old.Size, @new.Size));
        }

        var oldText = TextContent.From(oldBytes.Value);
        var newText = TextContent.From(newBytes.Value);
        if (oldText.IsBinary || newText.IsBinary)
        {
            return Section(title, old, @new, path, new BinaryOutcome());
        }

        if (oldText.Lines.Count > DiffLimits.MaxLines || newText.Lines.Count > DiffLimits.MaxLines)
        {
            return Section(title, old, @new, path, new TooLargeOutcome(old.Size, @new.Size));
        }

        if (old.Entry is not null && @new.Entry is not null && oldText.Encoding != newText.Encoding)
        {
            notes.Add($"The encoding differs ({Name(old.Kind)}: {EncodingName(oldText.Encoding)}, {Name(@new.Kind)}: {EncodingName(newText.Encoding)}).");
        }

        if (TextComparison.OnlyLineEndingsDiffer(oldText, newText))
        {
            return Section(title, old, @new, path, new EolOnlyOutcome(oldText.LineEndings, newText.LineEndings));
        }

        var diff = UnifiedDiff.Create(oldText, newText, query.Context);
        return Section(title, old, @new, path, new TextOutcome(query.StatOnly ? [] : diff.Hunks, diff.Added, diff.Removed));
    }

    private static DiffSection Section(string title, Side old, Side @new, SharePath path, DiffOutcome outcome) =>
        new(title, old.Kind, @new.Kind, Label(old, path), Label(@new, path), outcome);

    private static string Label(Side side, SharePath path) => side.Entry is null ? "/dev/null" : $"{Name(side.Kind)}/{path.Value}";

    internal static string Name(DiffSideKind kind) => kind switch
    {
        DiffSideKind.Local => "local",
        DiffSideKind.Remote => "remote",
        _ => "base",
    };

    private static string EncodingName(TextEncodingKind? encoding) => encoding switch
    {
        TextEncodingKind.Utf8Bom => "UTF-8 with BOM",
        TextEncodingKind.Utf16LittleEndian => "UTF-16 LE",
        TextEncodingKind.Utf16BigEndian => "UTF-16 BE",
        _ => "UTF-8",
    };

    private static FileEntry? Recorded(ManifestEntry? entry) => entry as FileEntry;

    private static string Describe(ManifestEntry? entry) => entry switch
    {
        FileEntry => "a file",
        DirectoryEntry => "a folder",
        _ => "nothing",
    };

    /// <summary>Reads HEAD at most once per query, and only when a path needs it.</summary>
    private sealed class HeadReader(DiffSources sources, Domain.Shares.ShareId shareId)
    {
        private Result<Manifest>? _head;

        public async Task<Result<Manifest>> ReadAsync(CancellationToken cancellationToken) =>
            _head ??= await sources.HeadAsync(shareId, cancellationToken);
    }
}
