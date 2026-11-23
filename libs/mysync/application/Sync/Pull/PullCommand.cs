using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentValidation;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Abstractions.Hashing;
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
using MySync.Domain.Sync.Pull;
using SharedKernel;

namespace MySync.Application.Sync.Pull;

/// <summary>
/// <c>mysync pull &lt;share&gt; [&lt;share-path&gt;...]</c> (CLAUDE.md §10c): a read-only download of the
/// share, or parts of it, into an ordinary folder. Never uploads, commits or changes the share,
/// never writes sync state, and never deletes or silently replaces a local file.
/// </summary>
/// <param name="Share">The share, by name or ID.</param>
/// <param name="Paths">Folders and files in the share (<c>/</c>-separated); none means the whole share.</param>
/// <param name="To">The destination folder (<c>--to</c>); the current folder by default. Created if missing.</param>
/// <param name="Latest">Each host's newest push folder (<c>--latest</c>).</param>
/// <param name="Hosts">With <paramref name="Latest"/>: only these hosts (<c>--host</c>, ignoring case).</param>
/// <param name="Overwrite">Replace files whose content differs (<c>--overwrite</c>); a file/folder clash still fails.</param>
public sealed record PullCommand(
    ShareReference Share,
    IReadOnlyList<string> Paths,
    string? To = null,
    bool Latest = false,
    IReadOnlyList<string>? Hosts = null,
    bool Overwrite = false,
    bool DryRun = false,
    int? Parallelism = null,
    ISyncProgressReporter? Progress = null) : ICommand<PullReport>;

/// <summary>What a pull did (or, with <c>--dry-run</c>, would do). Skipped paths make it exit 9; it never has conflicts.</summary>
/// <param name="Revision">The one revision everything came from.</param>
/// <param name="Destination">The destination folder's full path.</param>
/// <param name="Selected">Each share path taken and where it landed (empty strings: the share root, the destination root).</param>
/// <param name="Files">Files in the pull, skipped ones not counted: downloaded plus already there.</param>
/// <param name="Downloaded">Files written by this run (or that a dry run would write).</param>
/// <param name="AlreadyThere">Files the destination already had with the same content: left alone.</param>
/// <param name="Overwritten">Of <paramref name="Downloaded"/>, files that replaced a differing local file (<c>--overwrite</c>).</param>
/// <param name="Folders">Empty folders the share records, created (or already there).</param>
public sealed record PullReport(
    ShareId ShareId,
    ShareName? ShareName,
    Revision Revision,
    string Destination,
    bool DryRun,
    bool Latest,
    IReadOnlyList<PullRoot> Selected,
    int Files,
    long Bytes,
    TransferTotals Downloaded,
    TransferTotals AlreadyThere,
    int Overwritten,
    int Folders,
    ImmutableArray<SkippedPath> Skipped,
    ImmutableArray<string> Warnings)
{
    /// <summary>The selection held nothing (an empty share or folder): nothing was written.</summary>
    public bool NothingToPull => Files == 0 && Folders == 0 && Skipped.IsEmpty;
}

internal sealed class PullCommandValidator : AbstractValidator<PullCommand>
{
    public PullCommandValidator()
    {
        RuleFor(command => command.Share).NotNull();
        RuleFor(command => command.Paths).NotNull();
        RuleForEach(command => command.Paths).Custom((path, context) =>
        {
            if (SharePath.Create(path ?? string.Empty) is { IsFailure: true } invalid)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(nameof(PullCommand.Paths), invalid.Error.Description) { ErrorCode = invalid.Error.Code });
            }
        });
        RuleFor(command => command.Paths)
            .Must((command, paths) => !command.Latest || paths.Count == 0)
            .WithErrorCode(PullErrors.LatestWithPaths().Code)
            .WithMessage(PullErrors.LatestWithPaths().Description);
        RuleFor(command => command.Hosts)
            .Must((command, hosts) => command.Latest || hosts is not { Count: > 0 })
            .WithErrorCode(PullErrors.HostNeedsLatest().Code)
            .WithMessage(PullErrors.HostNeedsLatest().Description);
        RuleForEach(command => command.Hosts).NotEmpty().WithErrorCode("Pull.HostEmpty").WithMessage("--host needs a name.");
        RuleFor(command => command.To)
            .Must(to => to is null || !string.IsNullOrWhiteSpace(to))
            .WithErrorCode("Pull.ToEmpty")
            .WithMessage("--to needs a folder.");
        RuleFor(command => command.Parallelism).ValidParallelism();
    }
}

internal sealed class PullCommandHandler(
    ILocalFolderFactory destinations,
    ILocalSources sources,
    IShareStore store,
    ShareResolver resolver,
    ObjectTransfers transfers,
    IContentHasher hasher,
    SyncOptions options)
    : ICommandHandler<PullCommand, PullReport>
{
    private const int ShownOccupied = 5;

    public async Task<Result<PullReport>> Handle(PullCommand command, CancellationToken cancellationToken)
    {
        ISyncProgressReporter progress = command.Progress ?? NullSyncProgressReporter.Instance;
        int parallelism = command.Parallelism ?? options.MaxParallelism;
        ILocalFolder destination = destinations.OpenPullDestination(command.To);

        Result<PulledShare> share = await ReadShareAsync(command.Share, cancellationToken);
        if (share.IsFailure)
        {
            return Result.Failure<PullReport>(share.Error);
        }

        // HEAD is read once: everything in this pull comes from that one revision.
        Manifest head = share.Value.Head;
        Result<PullSelection> selected = command.Latest
            ? PullSelection.Latest(head.Entries, command.Hosts ?? [], share.Value.Label)
            : PullSelection.Select(head.Entries, [.. command.Paths.Select(path => SharePath.Create(path).Value)]);
        if (selected.IsFailure)
        {
            return Result.Failure<PullReport>(selected.Error);
        }

        PullSelection selection = selected.Value;
        Outcome outcome = new();

        if (selection.IsEmpty)
        {
            return Report(share.Value, command, destination, selection, outcome);
        }

        Result notTheShare = await CheckNotSyncedWithAsync(destination, share.Value, outcome, cancellationToken);
        if (notTheShare.IsFailure)
        {
            return Result.Failure<PullReport>(notTheShare.Error);
        }

        (List<PullFile> files, List<SharePath> emptyFolders) = LeaveOutUnwritable(destination, selection, outcome);

        Result<Plan> planned = await PlanAsync(destination, files, emptyFolders, command.Overwrite, parallelism, cancellationToken);
        if (planned.IsFailure)
        {
            return Result.Failure<PullReport>(planned.Error);
        }

        Plan plan = planned.Value;
        foreach (PullFile file in plan.AlreadyThere)
        {
            outcome.AlreadyThere(file);
        }

        if (command.DryRun)
        {
            foreach ((PullFile file, bool replaces) in plan.Downloads)
            {
                outcome.Downloaded(file, replaces);
            }

            outcome.FoldersCreated(plan.Folders.Count + plan.FoldersThere);
            return Report(share.Value, command, destination, selection, outcome);
        }

        await WriteAsync(share.Value.ShareId, destination, plan, outcome, progress, parallelism, cancellationToken);
        return Report(share.Value, command, destination, selection, outcome);
    }

    private async Task<Result<PulledShare>> ReadShareAsync(ShareReference reference, CancellationToken cancellationToken)
    {
        Result<ResolvedShare> resolved = await resolver.ResolveAsync(reference, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<PulledShare>(resolved.Error);
        }

        ShareId shareId = resolved.Value.ShareId;
        Result<ManifestSnapshot?> head = await store.GetManifestAsync(shareId, cancellationToken);
        if (head.IsFailure)
        {
            return Result.Failure<PulledShare>(head.Error);
        }

        if (head.Value is not { } snapshot)
        {
            return resolved.Value.Name is { } name
                ? Result.Failure<PulledShare>(ShareErrors.Incomplete(name, shareId))
                : Result.Failure<PulledShare>(ShareErrors.NotFound(shareId));
        }

        return snapshot.Manifest.Deletion is { } deletion
            ? Result.Failure<PulledShare>(ShareErrors.Deleted(shareId, deletion))
            : new PulledShare(shareId, resolved.Value.Name, snapshot.Manifest);
    }

    /// <summary>
    /// Files pulled into a folder that syncs with this share would be uploaded again by its next
    /// sync, as new files: refused, whatever the options. A folder of another share is fine, with a warning.
    /// </summary>
    private async Task<Result> CheckNotSyncedWithAsync(ILocalFolder destination, PulledShare share, Outcome outcome, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<SyncedFolder>> synced = await sources.FindSyncedFoldersAsync(destination.RootPath, cancellationToken);
        if (synced.IsFailure)
        {
            return Result.Failure(synced.Error);
        }

        if (synced.Value.FirstOrDefault(found => found.ShareId == share.ShareId) is { } same)
        {
            return Result.Failure(PullErrors.DestinationIsTheShare(same.FullPath, share.Label));
        }

        foreach (ShareId other in synced.Value.Select(found => found.ShareId).Distinct())
        {
            outcome.Warn($"This folder syncs with {other}; pulled files will be uploaded to it by its next sync.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Names this file system can't hold (Windows-invalid names on Windows; paths differing only in
    /// case on a case-insensitive disk, neither written) and mysync's own artifact names (a manifest
    /// is untrusted: it must not plant a <c>.mysynctool</c>) are skipped, never fatal.
    /// </summary>
    private static (List<PullFile> Files, List<SharePath> EmptyFolders) LeaveOutUnwritable(ILocalFolder destination, PullSelection selection, Outcome outcome)
    {
        IReadOnlyList<CaseCollision> collisions = destination.IsCaseSensitive
            ? []
            : PathRules.FindCaseCollisions([.. selection.Files.Select(file => file.LocalPath), .. selection.EmptyFolders]);

        bool Writable(SharePath path)
        {
            if (path.Segments.Any(segment => StateFileNames.AlwaysIgnored.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            {
                outcome.Skip(new SkippedPath(path.Value, SkipCause.InvalidName, "mysync's own file names are never written"));
                return false;
            }

            if (collisions.FirstOrDefault(collision => collision.Affects(path)) is { } collision)
            {
                outcome.Skip(new SkippedPath(path.Value, SkipCause.CaseCollision, $"differs only in case from {string.Join(", ", collision.Spellings.Select(PathErrors.Display))}"));
                return false;
            }

            if (destination.RequiresWindowsCompatibleNames && PathRules.CheckWindowsCompatible(path) is { IsFailure: true } windows)
            {
                outcome.Skip(new SkippedPath(path.Value, SkipCause.WindowsInvalidName, windows.Error.Description));
                return false;
            }

            return true;
        }

        return ([.. selection.Files.Where(file => Writable(file.LocalPath))], [.. selection.EmptyFolders.Where(Writable)]);
    }

    /// <summary>
    /// Every path the pull would write, checked before anything is downloaded: free → download; the
    /// same content → leave it; different content → in the way (or, with <c>--overwrite</c>, replace
    /// it); a folder where a file goes, a file where a folder goes, or a symlink on the way → in the way.
    /// Anything in the way fails the whole pull.
    /// </summary>
    private async Task<Result<Plan>> PlanAsync(
        ILocalFolder destination, List<PullFile> files, List<SharePath> emptyFolders, bool overwrite, int parallelism, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<SharePath, Task<Result<LocalEntryKind>>> kinds = new();
        ConcurrentBag<SharePath> occupied = [];
        ConcurrentBag<(PullFile File, bool Replaces)> downloads = [];
        ConcurrentBag<PullFile> alreadyThere = [];

        Task<Result<LocalEntryKind>> KindOf(SharePath path) => kinds.GetOrAdd(path, key => destination.GetEntryKindAsync(key, cancellationToken));

        async Task<bool> ParentsAreFoldersAsync(SharePath path)
        {
            for (SharePath? parent = path.Parent; parent is not null; parent = parent.Parent)
            {
                Result<LocalEntryKind> kind = await KindOf(parent);
                if (kind.IsFailure || kind.Value is LocalEntryKind.File or LocalEntryKind.Other)
                {
                    occupied.Add(parent);
                    return false;
                }
            }

            return true;
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                if (!await ParentsAreFoldersAsync(file.LocalPath))
                {
                    return;
                }

                Result<LocalEntryKind> kind = await KindOf(file.LocalPath);
                if (kind.IsFailure || kind.Value is LocalEntryKind.Directory or LocalEntryKind.Other)
                {
                    occupied.Add(file.LocalPath);
                }
                else if (kind.Value == LocalEntryKind.None)
                {
                    downloads.Add((file, false));
                }
                else if (await HasSameContentAsync(destination, file, ct))
                {
                    alreadyThere.Add(file);
                }
                else if (overwrite)
                {
                    downloads.Add((file, true));
                }
                else
                {
                    occupied.Add(file.LocalPath);
                }
            });

        List<SharePath> folders = [];
        int foldersThere = 0;
        foreach (SharePath folder in emptyFolders)
        {
            if (!await ParentsAreFoldersAsync(folder))
            {
                continue;
            }

            Result<LocalEntryKind> kind = await KindOf(folder);
            if (kind.IsFailure || kind.Value is LocalEntryKind.File or LocalEntryKind.Other)
            {
                occupied.Add(folder);
            }
            else if (kind.Value == LocalEntryKind.Directory)
            {
                foldersThere++;
            }
            else
            {
                folders.Add(folder);
            }
        }

        if (!occupied.IsEmpty)
        {
            List<string> paths = [.. occupied.Distinct().Select(path => path.Value).Order(StringComparer.Ordinal)];
            return Result.Failure<Plan>(PullErrors.DestinationOccupied(paths.Count, [.. paths.Take(ShownOccupied)], overwrite));
        }

        return new Plan(
            [.. downloads.OrderBy(download => download.File.LocalPath, SharePath.OrdinalComparer)],
            [.. alreadyThere.OrderBy(file => file.LocalPath, SharePath.OrdinalComparer)],
            folders,
            foldersThere);
    }

    /// <summary>Same size, then the same SHA-256. A file that can't be read counts as different.</summary>
    private async Task<bool> HasSameContentAsync(ILocalFolder destination, PullFile file, CancellationToken cancellationToken)
    {
        Result<LocalFile?> stat = await destination.GetFileAsync(file.LocalPath, cancellationToken);
        if (stat.IsFailure || stat.Value is not { } local || local.Size != file.Entry.Size)
        {
            return false;
        }

        Result<Stream> opened = await destination.OpenReadAsync(file.LocalPath, cancellationToken);
        if (opened.IsFailure)
        {
            return false;
        }

        try
        {
            await using Stream content = opened.Value;
            return await hasher.HashAsync(content, cancellationToken) == file.Entry.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads through sync's own path (decoded once, SHA-256 checked while it streams into
    /// <c>.mysync-pull.tmp/</c>, then moved into place with its mtime and executable bit). A file that
    /// fails is skipped and the rest go on. The staging folder is emptied before and after, also on Ctrl+C.
    /// </summary>
    private async Task WriteAsync(ShareId shareId, ILocalFolder destination, Plan plan, Outcome outcome, ISyncProgressReporter progress, int parallelism, CancellationToken cancellationToken)
    {
        progress.PhaseStarted(SyncPhase.Downloading);
        if (plan.Downloads.Count > 0)
        {
            progress.TransfersPlanned(TransferDirection.Download, plan.Downloads.Count, plan.Downloads.Sum(download => download.File.Entry.Size));
        }

        await destination.CleanStagingAsync(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(
                plan.Downloads,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (download, ct) =>
                {
                    (PullFile file, bool replaces) = download;
                    Result<IStagedFile> staged = await transfers.DownloadToStagingAsync(shareId, file.LocalPath, file.Entry, destination, progress, ct);
                    if (staged.IsFailure)
                    {
                        outcome.Skip(new SkippedPath(file.LocalPath.Value, SkipCause.ApplyFailed, staged.Error.Description));
                        return;
                    }

                    await using IStagedFile content = staged.Value;
                    Result placed = await destination.PlaceStagedFileAsync(content, file.LocalPath, file.Entry.MtimeUtc, file.Entry.Executable, ct);
                    if (placed.IsFailure)
                    {
                        outcome.Skip(new SkippedPath(file.LocalPath.Value, SkipCause.ApplyFailed, placed.Error.Description));
                        return;
                    }

                    progress.FileTransferred(TransferDirection.Download, file.LocalPath);
                    outcome.Downloaded(file, replaces);
                });

            foreach (SharePath folder in plan.Folders)
            {
                Result created = await destination.CreateDirectoryAsync(folder, cancellationToken);
                if (created.IsFailure)
                {
                    outcome.Skip(new SkippedPath(folder.Value, SkipCause.ApplyFailed, created.Error.Description));
                }
            }

            outcome.FoldersCreated(plan.Folders.Count + plan.FoldersThere);
        }
        finally
        {
            // Also after Ctrl+C: no partial file is left, under a final name or in staging.
            await destination.CleanStagingAsync(CancellationToken.None);
        }
    }

    private static PullReport Report(PulledShare share, PullCommand command, ILocalFolder destination, PullSelection selection, Outcome outcome)
    {
        TransferTotals downloaded = outcome.DownloadedTotals;
        TransferTotals alreadyThere = outcome.AlreadyThereTotals;

        return new PullReport(
            share.ShareId,
            share.Name,
            share.Head.Revision,
            destination.RootPath,
            command.DryRun,
            command.Latest,
            selection.Roots,
            downloaded.Files + alreadyThere.Files,
            downloaded.Bytes + alreadyThere.Bytes,
            downloaded,
            alreadyThere,
            outcome.Overwritten,
            outcome.Folders,
            [.. outcome.Skipped.OrderBy(skip => skip.Path, StringComparer.Ordinal)],
            [.. outcome.Warnings]);
    }

    private sealed record PulledShare(ShareId ShareId, ShareName? Name, Manifest Head)
    {
        public string Label => Name?.Value ?? ShareId.ToString();
    }

    /// <param name="FoldersThere">Empty folders the share records that the destination already has.</param>
    private sealed record Plan(
        IReadOnlyList<(PullFile File, bool Replaces)> Downloads,
        IReadOnlyList<PullFile> AlreadyThere,
        IReadOnlyList<SharePath> Folders,
        int FoldersThere);

    /// <summary>What happened, collected from parallel downloads.</summary>
    private sealed class Outcome
    {
        private readonly Lock _gate = new();
        private readonly List<SkippedPath> _skipped = [];
        private readonly List<string> _warnings = [];
        private int _downloadedFiles;
        private long _downloadedBytes;
        private int _thereFiles;
        private long _thereBytes;

        public int Overwritten { get; private set; }

        public int Folders { get; private set; }

        public IReadOnlyList<SkippedPath> Skipped
        {
            get
            {
                lock (_gate)
                {
                    return [.. _skipped];
                }
            }
        }

        public IReadOnlyList<string> Warnings => _warnings;

        public TransferTotals DownloadedTotals => new(_downloadedFiles, _downloadedBytes);

        public TransferTotals AlreadyThereTotals => new(_thereFiles, _thereBytes);

        public void Warn(string warning) => _warnings.Add(warning);

        public void Skip(SkippedPath skip)
        {
            lock (_gate)
            {
                _skipped.Add(skip);
            }
        }

        public void Downloaded(PullFile file, bool replaced)
        {
            lock (_gate)
            {
                _downloadedFiles++;
                _downloadedBytes += file.Entry.Size;
                if (replaced)
                {
                    Overwritten++;
                }
            }
        }

        public void AlreadyThere(PullFile file)
        {
            _thereFiles++;
            _thereBytes += file.Entry.Size;
        }

        public void FoldersCreated(int count) => Folders = count;
    }
}
