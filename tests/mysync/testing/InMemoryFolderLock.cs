using MySync.Application.Abstractions.State;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Testing;

/// <summary>The folder lock in memory: a second acquire while held fails with <c>State.FolderLocked</c>.</summary>
public sealed class InMemoryFolderLock : IFolderLock
{
    private int _held;

    private int _acquisitions;

    public bool IsHeld => Volatile.Read(ref _held) == 1;

    /// <summary>How many times the lock was taken: a read-only command must leave this at 0.</summary>
    public int Acquisitions => Volatile.Read(ref _acquisitions);

    public Task<Result<IAsyncDisposable>> AcquireAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _acquisitions);
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            return Task.FromResult(Result.Failure<IAsyncDisposable>(StateErrors.FolderLocked));
        }

#pragma warning disable CA2000 // Ownership passes to the caller, who disposes it to release the lock.
        return Task.FromResult(Result.Success<IAsyncDisposable>(new Handle(this)));
#pragma warning restore CA2000
    }

    /// <summary>True once <see cref="RemoveAsync"/> removed the (simulated) lock file.</summary>
    public bool Removed { get; private set; }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        Removed = !IsHeld;
        return Task.CompletedTask;
    }

    /// <summary>Simulates another mysync process holding the lock until the returned handle is disposed.</summary>
    public IAsyncDisposable HoldElsewhere()
    {
        Interlocked.Exchange(ref _held, 1);
        return new Handle(this);
    }

    private sealed class Handle(InMemoryFolderLock owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner._held, 0);
            return ValueTask.CompletedTask;
        }
    }
}
