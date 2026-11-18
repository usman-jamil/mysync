using MySync.Application.Abstractions.State;
using MySync.Domain.State;
using SharedKernel;

namespace MySync.Testing;

/// <summary><c>.mysynctool</c> in memory. Set <see cref="LoadError"/> to simulate a damaged file.</summary>
public sealed class InMemoryShareStateStore : IShareStateStore
{
    public ShareState? State { get; set; }

    public Error? LoadError { get; set; }

    public Error? SaveError { get; set; }

    public int Saves { get; private set; }

    public Task<Result<ShareState?>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LoadError is { } error ? Result.Failure<ShareState?>(error) : Result.Success(State));

    public Task<Result> SaveAsync(ShareState state, CancellationToken cancellationToken)
    {
        if (SaveError is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        State = state;
        Saves++;
        return Task.FromResult(Result.Success());
    }

    public Task<Result> DeleteAsync(CancellationToken cancellationToken)
    {
        State = null;
        return Task.FromResult(Result.Success());
    }
}
