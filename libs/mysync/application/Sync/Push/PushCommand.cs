using System.Collections.Immutable;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using MySync.Application.Abstractions.Environment;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Ignore;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Progress;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares;
using MySync.Application.Sync.Pipeline;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Domain.Sync.Push;
using SharedKernel;

namespace MySync.Application.Sync.Push;

/// <summary>
/// <c>mysync push &lt;share&gt; [&lt;path&gt;...]</c> (CLAUDE.md §10b): a one-way, add-only upload into
/// a new folder <c>&lt;host&gt;/&lt;UTC start&gt;/</c> of an existing share. Never downloads, never
/// deletes, never conflicts, and never reads or writes this machine's sync state.
/// </summary>
/// <param name="Share">The share, by name or ID.</param>
/// <param name="Paths">Files and folders to push; none means the current folder's contents.</param>
/// <param name="Host">The host folder's name (<c>--host</c>); the machine name by default.</param>
/// <param name="Create">Create the share (by name) if it doesn't exist.</param>
/// <param name="WriteInfo">Add <c>_mysync-push.json</c> to the target folder (off with <c>--no-info</c>).</param>
/// <param name="Force">Allow the home folder or a drive root as a source. Never overrides <c>Push.SourceIsTheShare</c>.</param>
public sealed record PushCommand(
    ShareReference Share,
    IReadOnlyList<string> Paths,
    string? Host = null,
    bool Create = false,
    bool WriteInfo = true,
    bool DryRun = false,
    bool Force = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null) : ICommand<PushReport>;

/// <summary>What a push did (or, with <c>--dry-run</c>, would do). Skipped paths make it exit 9; it never has conflicts.</summary>
/// <param name="ShareId"><c>null</c> only for a dry run of <c>--create</c> for a share that doesn't exist yet.</param>
/// <param name="Target">The target folder, e.g. <c>web01/20261121-051207Z</c> (with its suffix, if it got one).</param>
/// <param name="Revision">The revision committed; HEAD's for a dry run or when there was nothing to push.</param>
/// <param name="Created">The share was created by this push (<c>--create</c>), or would be.</param>
/// <param name="Files">Pushed files (the push record not counted) and their size.</param>
/// <param name="Uploaded">Files this run sent; <see cref="TransferTotals.StoredBytes"/> is what went over the wire.</param>
/// <param name="AlreadyStored">Files the share already stored (content-addressed): not sent again.</param>
/// <param name="IgnoredByShare">Pushed files the share's root <c>.mysyncignore</c> matches: machines that sync it won't download them.</param>
public sealed record PushReport(
    ShareId? ShareId,
    ShareName? ShareName,
    string Target,
    Revision? Revision,
    bool Committed,
    bool DryRun,
    bool Created,
    int Files,
    long Bytes,
    TransferTotals Uploaded,
    TransferTotals AlreadyStored,
    int CommitAttempts,
    ImmutableArray<SkippedPath> Skipped,
    ImmutableArray<string> Warnings,
    int IgnoredByShare)
{
    /// <summary>No file to push (after skips): nothing was committed.</summary>
    public bool NothingToPush => Files == 0;
}

internal sealed class PushCommandValidator : AbstractValidator<PushCommand>
{
    public PushCommandValidator()
    {
        RuleFor(command => command.Share).NotNull();
        RuleFor(command => command.Paths).NotNull();
        RuleForEach(command => command.Paths).NotEmpty().WithMessage("A path to push can't be empty.");
        RuleFor(command => command.Host)
            .Must(host => host is null || !string.IsNullOrWhiteSpace(host))
            .WithErrorCode("Push.HostEmpty")
            .WithMessage("--host needs a name.");
        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class PushCommandHandler(
    ILocalSources sources,
    IShareStore store,
    IAliasStore aliases,
    ShareResolver resolver,
    PushFiles files,
    ObjectTransfers transfers,
    ObjectCodec codec,
    RemoteCommitter committer,
    IContentHasher hasher,
    IIgnoreRulesLoader ignoreRules,
    IPushRecordSerializer recordSerializer,
    IMachineIdentity machine,
    IDateTimeProvider clock,
    TimeProvider timeProvider,
    SyncOptions options,
    ILogger<PushCommandHandler> logger)
    : ICommandHandler<PushCommand, PushReport>
{
    /// <summary>How often a push with <c>--create</c> looks again for a share another machine is creating right now.</summary>
    private const int CreatorWaits = 5;

    private const int ShownIgnoredPaths = 5;

    private static readonly SharePath RecordPath = SharePath.Create(PushRecord.FileName).Value;
    private static readonly SharePath IgnoreFilePath = SharePath.Create(StateFileNames.IgnoreFile).Value;

    public async Task<Result<PushReport>> Handle(PushCommand command, CancellationToken cancellationToken)
    {
        ISyncProgressReporter progress = command.Progress ?? NullSyncProgressReporter.Instance;
        DateTimeOffset started = Now();
        int parallelism = command.Parallelism ?? options.MaxParallelism;

        if (command.Create && command.Share.Name is null)
        {
            return Result.Failure<PushReport>(PushErrors.CreateNeedsName());
        }

        Result<PushTarget> target = PushTarget.Create(command.Host ?? machine.HostName, started);
        if (target.IsFailure)
        {
            return Result.Failure<PushReport>(target.Error);
        }

        Result<PickedSources> picked = PickSources(command);
        if (picked.IsFailure)
        {
            return Result.Failure<PushReport>(picked.Error);
        }

        Result<PushShare?> found = await FindShareAsync(command.Share, allowMissing: command.Create, waitForCreator: command.Create, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<PushReport>(found.Error);
        }

        PushShare? share = found.Value;
        if (share is not null)
        {
            Result notTheShare = await CheckNotSyncedWithAsync(picked.Value, share, cancellationToken);
            if (notTheShare.IsFailure)
            {
                return Result.Failure<PushReport>(notTheShare.Error);
            }
        }

        progress.PhaseStarted(SyncPhase.Scanning);
        Result<SourceListing> listed = await sources.ListAsync(picked.Value.ToList, cancellationToken);
        if (listed.IsFailure)
        {
            return Result.Failure<PushReport>(listed.Error);
        }

        SourceListing listing = listed.Value;
        List<SkippedPath> skipped = [.. picked.Value.Skipped, .. listing.Skipped.Select(ToSkip)];
        List<string> warnings = [];

        if (listing.Files.IsEmpty)
        {
            return NothingToPush(share, command, target.Value, skipped, warnings, created: share is null && command.DryRun);
        }

        bool created = false;
        if (share is null && !command.DryRun)
        {
            Result<PushShare> made = await CreateOrJoinAsync(command.Share.Name!, picked.Value, cancellationToken);
            if (made.IsFailure)
            {
                return Result.Failure<PushReport>(made.Error);
            }

            share = made.Value;
            created = share.CreatedHere;
        }

        // Fail before anything is uploaded if this host's folder can't go into the share.
        Result<SharePath> folder = share is null
            ? target.Value.Candidate(1)
            : PushPlanner.ChooseFolder(share.Head.Manifest.Entries, target.Value);
        if (folder.IsFailure)
        {
            return Result.Failure<PushReport>(folder.Error);
        }

        CompressionSetting compression = share?.Head.Manifest.Compression ?? CompressionSetting.None;
        PushFilesOutcome read = await files.RunAsync(share?.ShareId, listing.Files, compression, upload: !command.DryRun, progress, parallelism, cancellationToken);
        skipped.AddRange(read.Skipped);

        if (read.Files.Count == 0)
        {
            return NothingToPush(share, command, target.Value, skipped, warnings, created: created || share is null && command.DryRun);
        }

        List<PushedEntry> pushed = [
            .. read.Files.Select(file => new PushedEntry(file.Path, file.Entry)),
            .. listing.EmptyFolders.Select(path => new PushedEntry(path, DirectoryEntry.Instance))];
        warnings.AddRange(NameWarnings(pushed));

        bool withRecord = command.WriteInfo;
        if (withRecord && pushed.Any(entry => entry.Path == RecordPath))
        {
            withRecord = false;
            warnings.Add($"A pushed file is named {PushRecord.FileName} at the target folder's root, so the push record was left out.");
        }

        if (command.DryRun)
        {
            if (share is not null)
            {
                IgnoredByShare ignored = await CheckShareIgnoreAsync(share, share.Head.Manifest, folder.Value, pushed, cancellationToken);
                warnings.AddRange(ignored.Warnings);
                return Report(share, command, folder.Value, share.Head.Manifest.Revision, committed: false, created: false, read, 0, skipped, warnings, ignored.Count);
            }

            return Report(null, command, folder.Value, null, committed: false, created: true, read, 0, skipped, warnings, 0);
        }

        IReadOnlyList<string> sourcePaths = [.. picked.Value.Chosen.Select(source => source.Info.FullPath)];
        return await CommitAsync(command, share!, target.Value, started, sourcePaths, pushed, withRecord, read, skipped, warnings, created, progress, cancellationToken);
    }

    // The sources as given, each checked and mapped to where it lands, before anything is read.
    private Result<PickedSources> PickSources(PushCommand command)
    {
        List<(LocalSourceInfo Info, PushSourceRoot Root)> chosen = [];

        if (command.Paths.Count == 0)
        {
            LocalSourceInfo current = sources.CurrentFolder();
            chosen.Add((current, PushSourceLayout.CurrentFolder(current.FullPath)));
        }
        else
        {
            List<LocalSourceInfo> infos = [];
            foreach (string path in command.Paths)
            {
                LocalSourceInfo info = sources.Inspect(path);
                if (info.Kind == LocalSourceKind.Missing)
                {
                    return Result.Failure<PickedSources>(PushErrors.SourceNotFound(path));
                }

                infos.Add(info);
            }

            Result<IReadOnlyList<PushSourceRoot>> mapped = PushSourceLayout.Map([.. infos.Select(info =>
                new PushSource(info.Argument, info.Kind == LocalSourceKind.Folder ? PushSourceKind.Folder : PushSourceKind.File, info.Name))]);
            if (mapped.IsFailure)
            {
                return Result.Failure<PickedSources>(mapped.Error);
            }

            chosen.AddRange(infos.Zip(mapped.Value));
        }

        if (!command.Force && chosen.FirstOrDefault(source => source.Info.Kind == LocalSourceKind.Folder && source.Info.IsUnsafeRoot) is { Info: { } unsafeRoot })
        {
            return Result.Failure<PickedSources>(ShareErrors.UnsafeRoot(unsafeRoot.FullPath));
        }

        List<SkippedPath> skipped = [];
        List<SourceToList> toList = [];
        foreach ((LocalSourceInfo info, PushSourceRoot root) in chosen)
        {
            if (info.Kind == LocalSourceKind.Symlink)
            {
                skipped.Add(new SkippedPath(root.Root?.Value ?? info.Argument, SkipCause.Symlink, "symlinks and junctions are not pushed"));
            }
            else
            {
                toList.Add(new SourceToList(info.FullPath, info.Kind == LocalSourceKind.Folder, root.Root));
            }
        }

        return new PickedSources(chosen, toList, skipped);
    }

    /// <returns>The share and its HEAD, or <c>null</c> when it doesn't exist and <paramref name="allowMissing"/>.</returns>
    private async Task<Result<PushShare?>> FindShareAsync(ShareReference reference, bool allowMissing, bool waitForCreator, CancellationToken cancellationToken)
    {
        Result<ResolvedShare> resolved = await resolver.ResolveAsync(reference, cancellationToken);
        if (resolved.IsFailure)
        {
            return allowMissing && resolved.Error.Code == "Shares.NameNotFound"
                ? Result.Success<PushShare?>(null)
                : Result.Failure<PushShare?>(resolved.Error);
        }

        ShareId shareId = resolved.Value.ShareId;
        for (int wait = 0; wait <= CreatorWaits; wait++)
        {
            Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
            if (head.IsFailure)
            {
                return Result.Failure<PushShare?>(head.Error);
            }

            if (head.Value is { } snapshot)
            {
                return snapshot.Manifest.Deletion is { } deletion
                    ? Result.Failure<PushShare?>(ShareErrors.Deleted(shareId, deletion))
                    : new PushShare(shareId, resolved.Value.Name, snapshot, CreatedHere: false);
            }

            if (resolved.Value.Name is not { } name)
            {
                return Result.Failure<PushShare?>(ShareErrors.NotFound(shareId));
            }

            // A name with no manifest: an unfinished share, or another push --create a moment from committing it.
            if (!waitForCreator || wait >= CreatorWaits)
            {
                return Result.Failure<PushShare?>(ShareErrors.Incomplete(name, shareId));
            }

            await Task.Delay(committer.BackOff(wait + 1), timeProvider, cancellationToken);
        }

        return Result.Failure<PushShare?>(ShareErrors.NotFound(shareId));
    }

    /// <summary>
    /// <c>--create</c>: claim the name (<c>If-None-Match: *</c>), then commit an empty revision 1 at
    /// once, so other machines pushing with <c>--create</c> right now find a share to push into
    /// rather than an unfinished one. The files then go in as an ordinary push (revision 2). If
    /// another machine claimed the name first, this push joins that share.
    /// </summary>
    private async Task<Result<PushShare>> CreateOrJoinAsync(ShareName name, PickedSources picked, CancellationToken cancellationToken)
    {
        Result ready = await store.EnsureReadyForNewShareAsync(cancellationToken);
        if (ready.IsFailure)
        {
            return Result.Failure<PushShare>(ready.Error);
        }

        var shareId = ShareId.New();
        DateTimeOffset now = Now();
        Result<string> claimed = await aliases.CreateAsync(new AliasRecord(name, shareId, now, machine.Name), cancellationToken);
        if (claimed.IsFailure)
        {
            if (claimed.Error.Code != "Storage.AlreadyExists")
            {
                return Result.Failure<PushShare>(claimed.Error);
            }

            logger.LogInformation("Share {Name} was created by another machine meanwhile; pushing into it", name);
            Result<PushShare?> joined = await FindShareAsync(ShareReference.For(name), allowMissing: false, waitForCreator: true, cancellationToken);
            if (joined.IsFailure)
            {
                return Result.Failure<PushShare>(joined.Error);
            }

            Result notTheShare = await CheckNotSyncedWithAsync(picked, joined.Value!, cancellationToken);
            return notTheShare.IsFailure ? Result.Failure<PushShare>(notTheShare.Error) : joined.Value!;
        }

        Result<Manifest> empty = Manifest.CreateNew(shareId, [], now, machine.Name);
        if (empty.IsFailure)
        {
            return Result.Failure<PushShare>(empty.Error);
        }

        Result<string> committed = await store.CreateManifestAsync(shareId, empty.Value, cancellationToken);
        if (committed.IsFailure)
        {
            return Result.Failure<PushShare>(committed.Error);
        }

        await committer.WriteHistoryAsync(shareId, empty.Value, cancellationToken);
        return new PushShare(shareId, name, new ManifestSnapshot(empty.Value, committed.Value), CreatedHere: true);
    }

    /// <summary>
    /// A source folder that (or whose parent) already syncs with this share would come back as a
    /// second copy on its next sync: refused, whatever <c>--force</c> says.
    /// </summary>
    private async Task<Result> CheckNotSyncedWithAsync(PickedSources picked, PushShare share, CancellationToken cancellationToken)
    {
        // A file's folder is found by the source itself: Path here would split by this OS's rules.
        IEnumerable<string> paths = picked.Chosen.Select(source => source.Info.FullPath).Distinct(StringComparer.Ordinal);

        foreach (string path in paths)
        {
            Result<IReadOnlyList<SyncedFolder>> synced = await sources.FindSyncedFoldersAsync(path, cancellationToken);
            if (synced.IsFailure)
            {
                return Result.Failure(synced.Error);
            }

            if (synced.Value.FirstOrDefault(found => found.ShareId == share.ShareId) is { } same)
            {
                return Result.Failure(PushErrors.SourceIsTheShare(same.FullPath, share.Name?.Value ?? share.ShareId.ToString()));
            }
        }

        return Result.Success();
    }

    private async Task<Result<PushReport>> CommitAsync(
        PushCommand command,
        PushShare share,
        PushTarget target,
        DateTimeOffset started,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<PushedEntry> pushed,
        bool withRecord,
        PushFilesOutcome read,
        List<SkippedPath> skipped,
        List<string> warnings,
        bool created,
        ISyncProgressReporter progress,
        CancellationToken cancellationToken)
    {
        HashSet<Domain.Content.ContentHash> sent = [.. read.Sent];
        SharePath? committedFolder = null;
        bool recordLeftOut = false;

        progress.PhaseStarted(SyncPhase.Committing);
        Result<CommitOutcome> outcome = await committer.CommitAsync(
            share.ShareId,
            async (head, ct) =>
            {
                // Re-planned on every attempt: the same entries, added to whatever HEAD is now, in a folder still free there.
                Result<SharePath> folder = PushPlanner.ChooseFolder(head.Manifest.Entries, target);
                if (folder.IsFailure)
                {
                    return Result.Failure<Manifest?>(folder.Error);
                }

                List<PushedEntry> entries = [.. pushed];
                if (withRecord)
                {
                    PushedEntry? record = await UploadRecordAsync(share, head.Manifest, target, started, sourcePaths, folder.Value, read, skipped, sent, ct);
                    recordLeftOut = record is null;
                    if (record is not null)
                    {
                        entries.Add(record);
                    }
                }

                Result<PushPlan> plan = PushPlanner.Plan(head.Manifest, entries, target, Now(), machine.Name);
                if (plan.IsFailure)
                {
                    return Result.Failure<Manifest?>(plan.Error);
                }

                committedFolder = plan.Value.Folder;
                return plan.Value.Manifest;
            },
            cancellationToken,
            options.MaxPushCommitAttempts);

        if (outcome.IsFailure)
        {
            if (outcome.Error.Code == "Shares.Deleted")
            {
                await RemoveSentAsync(share.ShareId, sent, cancellationToken);
            }

            return Result.Failure<PushReport>(outcome.Error);
        }

        CommitOutcome done = outcome.Value;
        if (recordLeftOut)
        {
            warnings.Add($"The push record ({PushRecord.FileName}) could not be uploaded, so it was left out; the files were pushed.");
        }

        if (!done.HistoryWritten)
        {
            warnings.Add("The push was committed, but the history copy of its revision could not be written.");
        }

        IgnoredByShare ignored = await CheckShareIgnoreAsync(share, done.Remote.Manifest, committedFolder!, pushed, cancellationToken);
        warnings.AddRange(ignored.Warnings);

        return Report(share, command, committedFolder!, done.Committed!.Revision, committed: true, created, read, done.Attempts, skipped, warnings, ignored.Count);
    }

    /// <returns>The record's entry, or <c>null</c> if it couldn't be uploaded (the push goes on without it).</returns>
    private async Task<PushedEntry?> UploadRecordAsync(
        PushShare share,
        Manifest head,
        PushTarget target,
        DateTimeOffset started,
        IReadOnlyList<string> sourcePaths,
        SharePath folder,
        PushFilesOutcome read,
        IReadOnlyList<SkippedPath> skipped,
        HashSet<Domain.Content.ContentHash> sent,
        CancellationToken cancellationToken)
    {
        DateTimeOffset finished = Now();
        PushRecord record = new(
            target.Host,
            machine.UserName,
            machine.OperatingSystem,
            machine.MysyncVersion,
            started,
            finished,
            sourcePaths,
            folder.Value,
            read.Files.Count,
            read.Files.Sum(file => file.Entry.Size),
            [.. skipped.Select(skip => new PushRecordSkip(skip.Path, SkipCauseCodes.Of(skip.Cause), skip.Detail))]);

        byte[] bytes = recordSerializer.Serialize(record);
        using MemoryStream content = new(bytes, writable: false);
        Domain.Content.ContentHash hash = await hasher.HashAsync(content, cancellationToken);
        Result<FileEntry> entry = FileEntry.Create(hash, bytes.LongLength, finished, executable: false);
        if (entry.IsFailure)
        {
            return null;
        }

        Result<long?> uploaded = await transfers.UploadOneAsync(
            share.ShareId,
            RecordPath,
            entry.Value,
            _ => Task.FromResult(Result.Success<Stream>(new MemoryStream(bytes, writable: false))),
            head.Compression,
            NullSyncProgressReporter.Instance,
            cancellationToken);
        if (uploaded.IsFailure)
        {
            logger.LogWarning("Could not upload the push record: {Error}", uploaded.Error.Code);
            return null;
        }

        if (uploaded.Value is not null)
        {
            sent.Add(hash);
        }

        return new PushedEntry(RecordPath, entry.Value);
    }

    /// <summary>
    /// Clones apply the share's root <c>.mysyncignore</c> to downloads, so pushed files it matches
    /// would sit in the share unseen: they are pushed anyway, with a warning that says so.
    /// </summary>
    private async Task<IgnoredByShare> CheckShareIgnoreAsync(PushShare share, Manifest head, SharePath folder, IReadOnlyList<PushedEntry> pushed, CancellationToken cancellationToken)
    {
        if (head.Entries.GetValueOrDefault(IgnoreFilePath) is not FileEntry file)
        {
            return IgnoredByShare.None;
        }

        Result<string> rules = await ReadIgnoreFileAsync(share.ShareId, file, cancellationToken);
        if (rules.IsFailure)
        {
            return new IgnoredByShare(0, [$"The share's {StateFileNames.IgnoreFile} couldn't be read ({rules.Error.Description}), so the pushed files weren't checked against it."]);
        }

        IIgnoreMatcher matcher = ignoreRules.Compile(rules.Value);
        List<string> matched = [.. pushed
            .Where(entry => entry.Entry is FileEntry)
            .Select(entry => PushPlanner.Under(folder, entry.Path))
            .Where(path => matcher.IsIgnoredOrInsideIgnored(path, isDirectory: false))
            .Select(path => path.Value)];

        if (matched.Count == 0)
        {
            return IgnoredByShare.None;
        }

        string shown = string.Join(", ", matched.Take(ShownIgnoredPaths).Select(PathErrors.Display));
        return new IgnoredByShare(matched.Count, [string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{matched.Count} pushed files match the share's {StateFileNames.IgnoreFile}, so machines that sync this share won't download them: {shown}{(matched.Count > ShownIgnoredPaths ? ", …" : string.Empty)}")]);
    }

    private async Task<Result<string>> ReadIgnoreFileAsync(ShareId shareId, FileEntry file, CancellationToken cancellationToken)
    {
        const int MaxBytes = 1024 * 1024;
        if (file.Size > MaxBytes)
        {
            return Result.Failure<string>(LocalFileErrors.Unreadable(StateFileNames.IgnoreFile, $"it is larger than {MaxBytes / 1024} KB"));
        }

        Result<Stream> opened = await codec.OpenDecodedAsync(shareId, file, cancellationToken);
        if (opened.IsFailure)
        {
            return Result.Failure<string>(opened.Error);
        }

        try
        {
            await using Stream stream = opened.Value;
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            if (await hasher.HashAsync(buffer, cancellationToken) != file.Sha256)
            {
                return Result.Failure<string>(StorageErrors.CorruptObject(StateFileNames.IgnoreFile, "it doesn't match its hash"));
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer.ToArray()).TrimStart('﻿');
        }
        catch (IOException exception)
        {
            return Result.Failure<string>(LocalFileErrors.Unreadable(StateFileNames.IgnoreFile, exception.Message));
        }
    }

    private static IEnumerable<string> NameWarnings(IReadOnlyList<PushedEntry> pushed)
    {
        foreach (PushedEntry entry in pushed)
        {
            Result windows = PathRules.CheckWindowsCompatible(entry.Path);
            if (windows.IsFailure)
            {
                yield return $"{windows.Error.Description} Windows machines won't be able to download it.";
            }
        }

        foreach (CaseCollision collision in PathRules.FindCaseCollisions(pushed.Select(entry => entry.Path)))
        {
            yield return $"These pushed paths differ only in case, so case-insensitive machines (Windows, macOS) won't be able to download them: {string.Join(", ", collision.Spellings.Select(PathErrors.Display))}";
        }
    }

    private async Task RemoveSentAsync(ShareId shareId, IEnumerable<Domain.Content.ContentHash> sent, CancellationToken cancellationToken)
    {
        foreach (Domain.Content.ContentHash hash in sent)
        {
            Result removed = await store.DeleteObjectAsync(shareId, hash, cancellationToken);
            if (removed.IsFailure)
            {
                logger.LogWarning("Could not remove an object pushed to the deleted share: {Error}", removed.Error.Code);
            }
        }
    }

    private static PushReport NothingToPush(PushShare? share, PushCommand command, PushTarget target, List<SkippedPath> skipped, List<string> warnings, bool created) =>
        new(
            share?.ShareId,
            share?.Name ?? command.Share.Name,
            target.Candidate(1).Value,
            share?.Head.Manifest.Revision,
            Committed: false,
            command.DryRun,
            created,
            0,
            0,
            TransferTotals.None,
            TransferTotals.None,
            0,
            [.. skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)],
            [.. warnings],
            0);

    private static PushReport Report(
        PushShare? share,
        PushCommand command,
        SharePath folder,
        Revision? revision,
        bool committed,
        bool created,
        PushFilesOutcome read,
        int attempts,
        List<SkippedPath> skipped,
        List<string> warnings,
        int ignored)
    {
        List<CapturedFile> sentFiles = [.. read.Files.Where(file => file.Uploaded)];
        List<CapturedFile> storedFiles = [.. read.Files.Where(file => !file.Uploaded)];

        return new PushReport(
            share?.ShareId,
            share?.Name ?? command.Share.Name,
            folder.Value,
            revision,
            committed,
            command.DryRun,
            created,
            read.Files.Count,
            read.Files.Sum(file => file.Entry.Size),
            new TransferTotals(sentFiles.Count, sentFiles.Sum(file => file.Entry.Size), sentFiles.Sum(file => file.StoredBytes)),
            new TransferTotals(storedFiles.Count, storedFiles.Sum(file => file.Entry.Size)),
            attempts,
            [.. skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)],
            [.. warnings],
            ignored);
    }

    private static SkippedPath ToSkip(SkippedLocalPath local) => new(local.Path, local.Reason switch
    {
        LocalSkipReason.Unreadable => SkipCause.Unreadable,
        LocalSkipReason.InvalidName => SkipCause.InvalidName,
        LocalSkipReason.Symlink => SkipCause.Symlink,
        _ => SkipCause.Unstable,
    }, local.Detail);

    private DateTimeOffset Now() => new(clock.UtcNow, TimeSpan.Zero);

    private sealed record PickedSources(
        IReadOnlyList<(LocalSourceInfo Info, PushSourceRoot Root)> Chosen,
        IReadOnlyList<SourceToList> ToList,
        IReadOnlyList<SkippedPath> Skipped);

    /// <param name="CreatedHere">This push created it (<c>--create</c>).</param>
    private sealed record PushShare(ShareId ShareId, ShareName? Name, ManifestSnapshot Head, bool CreatedHere);

    private sealed record IgnoredByShare(int Count, IReadOnlyList<string> Warnings)
    {
        public static IgnoredByShare None { get; } = new(0, []);
    }
}
