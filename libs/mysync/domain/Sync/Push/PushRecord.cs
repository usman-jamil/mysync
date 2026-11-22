namespace MySync.Domain.Sync.Push;

/// <summary>
/// <c>_mysync-push.json</c>: one small file in each push's target folder, so whoever opens it later
/// knows where the data came from (CLAUDE.md §10b). An ordinary file in the share. Never holds
/// secrets: no environment variables, no connection strings, no command line beyond the sources.
/// </summary>
/// <param name="Sources">The full path of each source (the current folder's, when no paths were given).</param>
/// <param name="Target">The target folder, e.g. <c>web01/20261121-051207Z</c>.</param>
/// <param name="Files">Pushed files, the record itself not counted.</param>
public sealed record PushRecord(
    string Host,
    string User,
    string OperatingSystem,
    string MysyncVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<string> Sources,
    string Target,
    int Files,
    long Bytes,
    IReadOnlyList<PushRecordSkip> Skipped)
{
    public const int FormatVersion = 1;

    /// <summary>The record's name at the target folder's root.</summary>
    public const string FileName = "_mysync-push.json";
}

/// <param name="Cause">Kebab-case, as in <c>--json</c> output (<c>unreadable</c>, <c>unstable</c>, …).</param>
public sealed record PushRecordSkip(string Path, string Cause, string Detail);
