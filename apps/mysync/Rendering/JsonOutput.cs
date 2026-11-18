using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySync.Application.Conflicts.List;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.Maintenance.Update;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Sync;
using SharedKernel;

namespace MySync.Cli.Rendering;

/// <summary>
/// <c>--json</c>: exactly one JSON document on stdout per command, success or failure (CLAUDE.md §4).
/// Source-generated, camelCase, timestamps in UTC. Every document carries the <c>exitCode</c> the
/// process ends with, so a script can read one or the other.
/// </summary>
internal static class JsonOutput
{
    private static readonly CliJsonContext Context = new(new JsonSerializerOptions(CliJsonContext.Default.Options)
    {
        // Paths are printed as written (é, not é); control characters are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static int Write(SyncReport report, string command, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        SyncJson json = new(
            command,
            report.ShareId.ToString(),
            report.Revision.Value,
            report.Committed,
            report.DryRun,
            Totals(report.Uploaded),
            Totals(report.Downloaded),
            report.DeletedLocally,
            report.DeletedRemotely,
            [.. report.Conflicts.Select(Conflict)],
            [.. report.Skipped.Select(skip => new SkippedJson(skip.Path, CauseCode(skip.Cause), skip.Detail))],
            [.. report.Warnings],
            report.DryRun ? [.. report.Plan.Actions.Where(action => action.Kind != SyncActionKind.NoChange).Select(action => new PlannedJson(action.Path.Value, ActionCode(action.Kind)))] : null,
            command == "resolve" ? [.. report.Resolved.Select(path => path.Value)] : null,
            exitCode);

        Emit(json, Context.SyncJson);
        return exitCode;
    }

    public static int Write(CreateShareResponse created, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(created);

        Emit(
            new CreateJson(
                "create",
                created.ShareId.ToString(),
                created.FileCount,
                created.TotalBytes,
                Totals(created.Uploaded),
                [.. created.Skipped.Select(skip => new SkippedJson(skip.Path, CauseCode(skip.Cause), skip.Detail))],
                [.. created.Warnings],
                $"mysync sync {created.ShareId}",
                exitCode),
            Context.CreateJson);
        return exitCode;
    }

    public static int Write(NewSharePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        Emit(new PreviewJson("create", DryRun: true, preview.RootPath, preview.FileCount, preview.TotalBytes, preview.IsUnsafeRoot, ExitCodes.Success), Context.PreviewJson);
        return ExitCodes.Success;
    }

    public static int Write(ConflictListResponse listed, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(listed);

        Emit(new ConflictListJson("conflicts", listed.ShareId.ToString(), [.. listed.Conflicts.Select(Conflict)], exitCode), Context.ConflictListJson);
        return exitCode;
    }

    public static int Write(ShareInfoResponse info, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(info);

        Emit(
            new InfoJson(
                "info",
                info.ShareId.ToString(),
                info.RootPath,
                info.Location.Provider,
                info.Location.AccountName,
                info.Location.Container,
                info.BaseRevision.Value,
                info.RemoteRevision.Value,
                info.RemoteUpdatedBy,
                Utc(info.RemoteUpdatedUtc),
                Utc(info.CreatedUtc),
                info.FileCount,
                info.TotalBytes,
                info.OpenConflicts,
                exitCode),
            Context.InfoJson);
        return exitCode;
    }

    public static int Write(DeleteShareResponse deleted, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(deleted);

        Emit(
            new DeleteJson(
                "delete",
                deleted.ShareId.ToString(),
                deleted.MarkerRevision.Value,
                Utc(deleted.Deletion.DeletedUtc),
                deleted.Deletion.DeletedBy,
                deleted.WasAlreadyDeleted,
                deleted.ObjectsDeleted,
                deleted.BytesDeleted,
                deleted.HistoryRevisionsSwept,
                deleted.LocalStateRemoved,
                deleted.RecoverableCopies,
                [.. deleted.Warnings],
                exitCode),
            Context.DeleteJson);
        return exitCode;
    }

    public static int Write(UpdateReport report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        string status = report.Status switch
        {
            UpdateStatus.UpToDate => "up-to-date",
            UpdateStatus.Available => "available",
            _ => "updated",
        };
        Emit(
            new UpdateJson("update", status, report.Current.ToString(), report.Target.ToString(), report.Downgrade, report.SignificantChange, report.NotesUrl, report.ExecutablePath, report.Source, exitCode),
            Context.UpdateJson);
        return exitCode;
    }

    public static int Write(GarbageReport report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        Emit(
            new GcJson(
                "gc",
                report.ShareId.ToString(),
                report.DryRun,
                report.Head.Value,
                [.. report.KeptRevisions.Select(revision => revision.Value)],
                [.. report.DeletedObjects.Select(item => new GcObjectJson(item.Hash.Value, item.Size, Utc(item.LastModifiedUtc)))],
                report.DeletedBytes,
                report.KeptObjects,
                report.ProtectedByGrace,
                [.. report.DeletedHistory.Select(revision => revision.Value)],
                exitCode),
            Context.GcJson);
        return exitCode;
    }

    public static int Write(Error error, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(error);

        IEnumerable<Error> details = error is ValidationError validation ? validation.Errors : [];
        Emit(
            new FailureJson(new ErrorJson(error.Code, error.Description, [.. details.Select(inner => new ErrorJson(inner.Code, inner.Description, null))]), exitCode),
            Context.FailureJson);
        return exitCode;
    }

    public static string CauseCode(SkipCause cause) => cause switch
    {
        SkipCause.Unstable => "unstable",
        SkipCause.Unreadable => "unreadable",
        SkipCause.InvalidName => "invalid-name",
        SkipCause.Symlink => "symlink",
        SkipCause.NoDelete => "no-delete",
        SkipCause.ChangedSinceScan => "changed-since-scan",
        SkipCause.ApplyFailed => "apply-failed",
        SkipCause.CaseCollision => "case-collision",
        SkipCause.WindowsInvalidName => "windows-invalid-name",
        SkipCause.IgnoredLocally => "ignored-locally",
        _ => cause.ToString(),
    };

    public static string ActionCode(SyncActionKind kind) => kind switch
    {
        SyncActionKind.Download => "download",
        SyncActionKind.UpdateLocalMode => "update-local-mode",
        SyncActionKind.CreateLocalDirectory => "create-local-directory",
        SyncActionKind.DeleteLocal => "delete-local",
        SyncActionKind.Upload => "upload",
        SyncActionKind.UpdateRemoteMode => "update-remote-mode",
        SyncActionKind.AddRemoteDirectory => "add-remote-directory",
        SyncActionKind.RemoveRemote => "remove-remote",
        SyncActionKind.Conflict => "conflict",
        SyncActionKind.Skip => "skip",
        _ => "no-change",
    };

    private static void Emit<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Console.Out.Write(JsonSerializer.Serialize(value, type));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    private static TotalsJson Totals(TransferTotals totals) => new(totals.Files, totals.Bytes);

    private static ConflictJson Conflict(Conflict conflict) => new(
        conflict.Path.Value,
        conflict.Kind.ToCode(),
        Entry(conflict.Base),
        Entry(conflict.Local),
        Entry(conflict.Remote),
        conflict.RemoteRevision.Value,
        conflict.RemoteUpdatedBy,
        Utc(conflict.FirstDetectedUtc),
        Utc(conflict.LastSeenUtc));

    private static EntryJson? Entry(ManifestEntry? entry) => entry switch
    {
        FileEntry file => new EntryJson("file", file.Sha256.Value, file.Size, Utc(file.MtimeUtc), file.Executable),
        DirectoryEntry => new EntryJson("dir", null, null, null, null),
        _ => null,
    };

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}

internal sealed record TotalsJson(int Files, long Bytes);

internal sealed record SkippedJson(string Path, string Cause, string Detail);

internal sealed record PlannedJson(string Path, string Action);

internal sealed record EntryJson(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MtimeUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Exec);

internal sealed record ConflictJson(
    string Path,
    string Kind,
    EntryJson? Base,
    EntryJson? Local,
    EntryJson? Remote,
    long RemoteRevision,
    string RemoteUpdatedBy,
    string FirstDetectedUtc,
    string LastSeenUtc);

internal sealed record SyncJson(
    string Command,
    string ShareId,
    long Revision,
    bool Committed,
    bool DryRun,
    TotalsJson Uploaded,
    TotalsJson Downloaded,
    int DeletedLocally,
    int DeletedRemotely,
    List<ConflictJson> Conflicts,
    List<SkippedJson> Skipped,
    List<string> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<PlannedJson>? Plan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? Resolved,
    int ExitCode);

internal sealed record ConflictListJson(string Command, string ShareId, List<ConflictJson> Conflicts, int ExitCode);

internal sealed record InfoJson(
    string Command,
    string ShareId,
    string RootPath,
    string Provider,
    string AccountName,
    string Container,
    long BaseRevision,
    long RemoteRevision,
    string RemoteUpdatedBy,
    string RemoteUpdatedUtc,
    string CreatedUtc,
    int FileCount,
    long TotalBytes,
    int OpenConflicts,
    int ExitCode);

internal sealed record DeleteJson(
    string Command,
    string ShareId,
    long MarkerRevision,
    string DeletedUtc,
    string DeletedBy,
    bool WasAlreadyDeleted,
    int ObjectsDeleted,
    long BytesDeleted,
    long HistoryRevisionsSwept,
    bool LocalStateRemoved,
    int RecoverableCopies,
    List<string> Warnings,
    int ExitCode);

internal sealed record UpdateJson(
    string Command,
    string Status,
    string CurrentVersion,
    string TargetVersion,
    bool Downgrade,
    bool SignificantChange,
    string? NotesUrl,
    string ExecutablePath,
    string Source,
    int ExitCode);

internal sealed record GcObjectJson(string Sha256, long Size, string LastModifiedUtc);

internal sealed record GcJson(
    string Command,
    string ShareId,
    bool DryRun,
    long HeadRevision,
    List<long> KeptRevisions,
    List<GcObjectJson> DeletedObjects,
    long DeletedBytes,
    int KeptObjects,
    int ProtectedByGrace,
    List<long> DeletedHistory,
    int ExitCode);

internal sealed record CreateJson(
    string Command,
    string ShareId,
    int FileCount,
    long TotalBytes,
    TotalsJson Uploaded,
    List<SkippedJson> Skipped,
    List<string> Warnings,
    string CloneCommand,
    int ExitCode);

internal sealed record PreviewJson(string Command, bool DryRun, string RootPath, int FileCount, long TotalBytes, bool IsUnsafeRoot, int ExitCode);

internal sealed record ErrorJson(
    string Code,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ErrorJson>? Details);

internal sealed record FailureJson(ErrorJson Error, int ExitCode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, IndentSize = 2, NewLine = "\n")]
[JsonSerializable(typeof(SyncJson))]
[JsonSerializable(typeof(CreateJson))]
[JsonSerializable(typeof(PreviewJson))]
[JsonSerializable(typeof(FailureJson))]
[JsonSerializable(typeof(ConflictListJson))]
[JsonSerializable(typeof(InfoJson))]
[JsonSerializable(typeof(GcJson))]
[JsonSerializable(typeof(DeleteJson))]
[JsonSerializable(typeof(UpdateJson))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
