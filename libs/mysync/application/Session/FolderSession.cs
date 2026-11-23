using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Conflicts.Resolve;
using MySync.Application.Sync.Push;
using MySync.Application.Sync.Run;
using MySync.Domain.Paths;
using MySync.Domain.Session;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Application.Session;

/// <summary>
/// The share folder a front end (<c>mysync tui</c>, later a web page) works on (CLAUDE.md §10d).
/// It holds the last status and runs <b>one job at a time</b> through the existing commands and
/// queries; it knows nothing about terminals. Start methods return at once (a failure only when a
/// job can't start); what happens is read from the events.
/// </summary>
public interface IFolderSession : IAsyncDisposable
{
    /// <summary>The last status a rescan produced; <c>null</c> before the first one.</summary>
    FolderStatus? Status { get; }

    /// <summary>The job running now, or <c>null</c>.</summary>
    SessionJob? Running { get; }

    /// <summary>The next event, if one is waiting.</summary>
    bool TryReadEvent(out SessionEvent? sessionEvent);

    /// <summary>Completes when an event is waiting.</summary>
    Task<bool> WaitForEventAsync(CancellationToken cancellationToken);

    Result Rescan();

    /// <summary>A sync, then a rescan. <paramref name="allowMassDelete"/> only after the user confirmed a mass delete.</summary>
    Result Sync(bool allowMassDelete = false, bool noDelete = false);

    /// <summary><c>resolve</c> for these conflicted paths (or every one), then a rescan.</summary>
    Result Resolve(IReadOnlyList<string> paths, bool all, KeepSide keep);

    /// <summary>
    /// <c>push</c> of one item of this folder (a file or folder, relative to it) to another share,
    /// with push's defaults. No rescan follows: a push changes nothing here.
    /// </summary>
    Result Push(string path, string share, bool create);

    /// <summary>The <c>diff</c> of one path (read-only; allowed while a job runs).</summary>
    Task<Result<DiffResponse>> DiffAsync(string path, CancellationToken cancellationToken);

    /// <summary>Cancels the running job, if any. Cancelling is always safe: a cancelled sync can be run again.</summary>
    void Cancel();

    /// <summary>Completes when no job is running.</summary>
    Task WhenIdleAsync();
}

internal sealed class FolderSession(IServiceScopeFactory scopes, TimeProvider time) : IFolderSession
{
    private readonly Lock _gate = new();
    private readonly SessionEventQueue _events = new();
    private ImmutableSortedDictionary<SharePath, HashCacheEntry>? _hashes;
    private CancellationTokenSource? _cancel;
    private Task _current = Task.CompletedTask;
    private FolderStatus? _status;
    private SessionJob? _running;

    public FolderStatus? Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public SessionJob? Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public bool TryReadEvent(out SessionEvent? sessionEvent) => _events.TryRead(out sessionEvent);

    public Task<bool> WaitForEventAsync(CancellationToken cancellationToken) => _events.WaitToReadAsync(cancellationToken).AsTask();

    public Result Rescan() => Start(SessionJob.Rescan, RescanAsync, rescanAfter: false);

    public Result Sync(bool allowMassDelete = false, bool noDelete = false) => Start(
        SessionJob.Sync,
        async (progress, ct) =>
        {
            Result<SyncReport> synced = await SendAsync<RunSyncCommand, SyncReport>(
                new RunSyncCommand(NoDelete: noDelete, AllowMassDelete: allowMassDelete, Progress: progress), ct);
            return synced.IsSuccess ? new JobResult(Sync: synced.Value) : Result.Failure<JobResult>(synced.Error);
        },
        rescanAfter: true);

    public Result Resolve(IReadOnlyList<string> paths, bool all, KeepSide keep)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string side = keep == KeepSide.Local ? "local" : "remote";
        return Start(
            SessionJob.Resolve,
            async (progress, ct) =>
            {
                Result<SyncReport> resolved = await SendAsync<ResolveConflictsCommand, SyncReport>(
                    new ResolveConflictsCommand(all ? [] : paths, all, side, Progress: progress), ct);
                return resolved.IsSuccess ? new JobResult(Sync: resolved.Value) : Result.Failure<JobResult>(resolved.Error);
            },
            rescanAfter: true);
    }

    public Result Push(string path, string share, bool create)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(share);

        // Only something inside this folder: a share path is relative, without "..".
        Result<SharePath> item = SharePath.Create(path.Trim().TrimEnd('/'));
        if (item.IsFailure)
        {
            return Result.Failure(item.Error);
        }

        Result<ShareReference> target = ShareReference.Parse(share);
        if (target.IsFailure)
        {
            return Result.Failure(target.Error);
        }

        if (Status is { } status)
        {
            if (IsOwnShare(target.Value, status))
            {
                return Result.Failure(SessionErrors.PushToOwnShare(status.ShareLabel));
            }

            if (!ExistsHere(item.Value, status))
            {
                return Result.Failure(SessionErrors.NothingOnDisk(item.Value.Value));
            }
        }

        SessionJob job = new(JobKind.Push, "push", item.Value.Value, share.Trim());
        return Start(
            job,
            async (progress, ct) =>
            {
                Result<PushReport> pushed = await SendAsync<PushCommand, PushReport>(
                    new PushCommand(target.Value, [item.Value.Value], Create: create, Progress: progress), ct);
                return pushed.IsSuccess ? new JobResult(Push: pushed.Value) : Result.Failure<JobResult>(pushed.Error);
            },
            rescanAfter: false);
    }

    public async Task<Result<DiffResponse>> DiffAsync(string path, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<DiffQuery, DiffResponse>>().Handle(new DiffQuery([path]), cancellationToken);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await WhenIdleAsync();
        lock (_gate)
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private static bool IsOwnShare(ShareReference target, FolderStatus status) =>
        target.Id == status.ShareId || target.Name is { } name && status.Names.Contains(name);

    // A file on disk, or a folder with something on disk below it.
    private static bool ExistsHere(SharePath item, FolderStatus status)
    {
        string prefix = item.Value + SharePath.Separator;
        return status.Entries.Any(entry =>
            entry.ExistsHere
            && (entry.Path == item.Value || entry.Path.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private Result Start(SessionJob job, Func<SessionProgress, CancellationToken, Task<Result<JobResult>>> work, bool rescanAfter)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_running is { } running)
            {
                return Result.Failure(SessionErrors.Busy(running.Name));
            }

            _running = job;
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            token = _cancel.Token;
            _current = Task.Run(() => RunAsync(job, work, rescanAfter, token), CancellationToken.None);
        }

        return Result.Success();
    }

    private async Task RunAsync(SessionJob job, Func<SessionProgress, CancellationToken, Task<Result<JobResult>>> work, bool rescanAfter, CancellationToken cancellationToken)
    {
        try
        {
            bool finished = await RunOneAsync(job, work, cancellationToken);
            if (finished && rescanAfter && !cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _running = SessionJob.Rescan;
                }

                await RunOneAsync(SessionJob.Rescan, RescanAsync, cancellationToken);
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    private async Task<bool> RunOneAsync(SessionJob job, Func<SessionProgress, CancellationToken, Task<Result<JobResult>>> work, CancellationToken cancellationToken)
    {
        _events.Write(new JobStarted(job));
        try
        {
            Result<JobResult> result = await work(new SessionProgress(_events, time), cancellationToken);
            if (result.IsFailure)
            {
                _events.Write(new JobFailed(job, result.Error));
                return false;
            }

            _events.Write(new JobFinished(job, result.Value));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _events.Write(new JobCancelled(job));
            return false;
        }
    }

    private async Task<Result<JobResult>> RescanAsync(SessionProgress progress, CancellationToken cancellationToken)
    {
        ImmutableSortedDictionary<SharePath, HashCacheEntry>? hashes;
        lock (_gate)
        {
            hashes = _hashes;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        Result<FolderStatus> status = await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetFolderStatusQuery, FolderStatus>>()
            .Handle(new GetFolderStatusQuery(hashes, progress), cancellationToken);
        if (status.IsFailure)
        {
            return Result.Failure<JobResult>(status.Error);
        }

        lock (_gate)
        {
            _status = status.Value;
            _hashes = status.Value.HashCache;
        }

        return new JobResult(Status: status.Value);
    }

    private async Task<Result<TResponse>> SendAsync<TCommand, TResponse>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResponse>
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, cancellationToken);
    }
}
