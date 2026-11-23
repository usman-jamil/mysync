using System.Collections.Immutable;
using MySync.Domain.Sync;

namespace MySync.Tui.Effects;

/// <summary>What Update asks the runtime to do: start a job, load a diff, cancel, quit. Update itself does no I/O.</summary>
public abstract record Effect;

public sealed record StartRescan : Effect;

public sealed record StartSync(bool AllowMassDelete) : Effect;

/// <summary>Equal when the paths are the same, in order (an array is otherwise compared by reference).</summary>
public sealed record StartResolve(ImmutableArray<string> Paths, bool All, KeepSide Keep) : Effect
{
    public bool Equals(StartResolve? other) =>
        other is not null && All == other.All && Keep == other.Keep && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(All, Keep, Paths.Length);
}

public sealed record StartPush(string Path, string Share, bool Create) : Effect;

public sealed record LoadDiff(string Path) : Effect;

public sealed record CancelJob : Effect;

/// <param name="ExitCode">0 for a normal quit; 130 when Ctrl+C ended it during a job.</param>
public sealed record Quit(int ExitCode) : Effect;
