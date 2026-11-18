using System.Collections.Concurrent;
using MySync.Application.Abstractions.Logging;
using MySync.Domain.Paths;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Testing;

/// <summary>Records <c>.mysynctool.log</c> writes as lines: "CONFLICT &lt;kind&gt; &lt;path&gt;" / "RESOLVED &lt;keep&gt; &lt;path&gt;".</summary>
public sealed class RecordingConflictLog : IConflictLog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public Error? FailWith { get; set; }

    public Task<Result> RecordDetectedAsync(Conflict conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return Record($"CONFLICT {conflict.Kind.ToCode()} {conflict.Path.Value}");
    }

    public Task<Result> RecordResolvedAsync(SharePath path, KeepSide kept, DateTimeOffset resolvedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Record($"RESOLVED {(kept == KeepSide.Local ? "keep-local" : "keep-remote")} {path.Value}");
    }

    /// <summary>True once <see cref="DeleteAsync"/> removed the log.</summary>
    public bool Deleted { get; private set; }

    public Task<Result> DeleteAsync(CancellationToken cancellationToken)
    {
        _lines.Clear();
        Deleted = true;
        return Task.FromResult(Result.Success());
    }

    private Task<Result> Record(string line)
    {
        if (FailWith is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        _lines.Enqueue(line);
        return Task.FromResult(Result.Success());
    }
}
