using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Conflicts.List;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.Maintenance.Update;
using MySync.Application.Publishing.List;
using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.List;
using MySync.Application.Shares.Rename;
using MySync.Application.Shares.Preview;
using MySync.Application.Sync.Run;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
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
            Totals(report.Uploaded, sent: true),
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
                created.Name.Value,
                created.FileCount,
                created.TotalBytes,
                Totals(created.Uploaded, sent: true),
                [.. created.Skipped.Select(skip => new SkippedJson(skip.Path, CauseCode(skip.Cause), skip.Detail))],
                [.. created.Warnings],
                $"mysync sync {created.Name}",
                created.Compression.ToCode(),
                exitCode),
            Context.CreateJson);
        return exitCode;
    }

    public static int Write(Application.Sync.Push.PushReport report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        Emit(
            new PushJson(
                "push",
                report.ShareId?.ToString(),
                report.ShareName?.Value,
                report.Target,
                report.Revision?.Value,
                report.Committed,
                report.DryRun,
                report.Created,
                report.Files,
                report.Bytes,
                Totals(report.Uploaded, sent: true),
                Totals(report.AlreadyStored),
                report.CommitAttempts,
                [.. report.Skipped.Select(skip => new SkippedJson(skip.Path, CauseCode(skip.Cause), skip.Detail))],
                [.. report.Warnings],
                report.IgnoredByShare,
                report.Compression.ToCode(),
                report.Filters is { } filters
                    ? new PushFiltersJson(
                        filters.UpdatedSinceUtc?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                        filters.UpdatedSinceSpec,
                        [.. filters.Include],
                        [.. filters.Exclude],
                        filters.MaxSizeBytes)
                    : null,
                new PushFilteredOutJson(
                    report.FilteredOut.Total, report.FilteredOut.NotChanged, report.FilteredOut.NotIncluded, report.FilteredOut.Excluded, report.FilteredOut.TooLarge),
                exitCode),
            Context.PushJson);
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
                [.. info.Names.Select(name => name.Value)],
                info.RootPath,
                info.Location.Provider,
                info.Location.AccountName,
                info.Location.Container,
                info.BaseRevision?.Value,
                info.RemoteRevision.Value,
                info.RemoteUpdatedBy,
                Utc(info.RemoteUpdatedUtc),
                Utc(info.CreatedUtc),
                info.FileCount,
                info.TotalBytes,
                info.OpenConflicts,
                info.Compression.ToCode(),
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
                deleted.MarkerRevision?.Value,
                deleted.Deletion is { } deletion ? Utc(deletion.DeletedUtc) : null,
                deleted.Deletion?.DeletedBy,
                deleted.WasAlreadyDeleted,
                deleted.WasIncomplete,
                [.. deleted.NamesFreed.Select(name => name.Value)],
                deleted.ObjectsDeleted,
                deleted.BytesDeleted,
                deleted.HistoryRevisionsSwept,
                deleted.LocalStateRemoved,
                deleted.RecoverableCopies,
                deleted.PublicationsRemoved,
                [.. deleted.Warnings],
                exitCode),
            Context.DeleteJson);
        return exitCode;
    }

    public static int Write(PublishReport report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        Emit(
            new PublishJson(
                "publish",
                report.ShareId.ToString(),
                report.PublishId.Value,
                report.Url.AbsoluteUri,
                report.Revision.Value,
                report.Title,
                report.Files,
                report.Bytes,
                report.Copies.ToDictionary(pair => CopyMethodName(pair.Key), pair => pair.Value, StringComparer.Ordinal),
                report.Verified,
                report.VerificationDetail,
                report.ShellCommand,
                report.PowerShellCommand,
                [.. report.Skipped.Select(skipped => new SkippedJson(skipped.Path.Value, "unpublishable", skipped.Reason))],
                [.. report.Warnings],
                exitCode),
            Context.PublishJson);
        return exitCode;
    }

    public static int Write(PublicationListResponse listed, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(listed);

        Emit(
            new PublicationsJson(
                "publications",
                listed.ShareId.ToString(),
                [.. listed.Publications.Select(publication => new PublicationJson(
                    publication.Id.Value,
                    publication.Status switch
                    {
                        PublicationStatus.Published => "published",
                        PublicationStatus.Pending => "pending",
                        _ => "failed",
                    },
                    publication.Title,
                    publication.Revision.Value,
                    publication.Files,
                    publication.Bytes,
                    Utc(publication.CreatedUtc),
                    publication.CreatedBy,
                    publication.PublishAccount,
                    publication.Url,
                    publication.SiteAssetsVersion))],
                exitCode),
            Context.PublicationsJson);
        return exitCode;
    }

    public static int Write(Application.Shares.Compression.CompressionResponse compression, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(compression);

        Emit(
            new CompressionJson(
                "compression",
                compression.ShareId.ToString(),
                compression.Name?.Value,
                compression.Setting.ToCode(),
                compression.Revision.Value,
                compression.FormatVersion,
                compression.Changed,
                exitCode),
            Context.CompressionJson);
        return exitCode;
    }

    public static int Write(DiffResponse diff, bool statOnly, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(diff);

        Emit(new DiffJson("diff", diff.ShareId.ToString(), [.. diff.Items.Select(item => DiffItem(item, statOnly))], exitCode), Context.DiffJson);
        return exitCode;
    }

    private static DiffItemJson DiffItem(PathDiff item, bool statOnly) => new(
        item.Path.Value,
        item.Kind?.ToCode(),
        DiffSide(item.Local),
        DiffSide(item.Remote),
        DiffSide(item.Base),
        [.. item.Sections.Select(section => DiffSection(section, statOnly))],
        [.. item.Notes],
        item.Kind is null ? null : new ResolveJson(DiffView.ResolveCommand(item.Path, "local"), DiffView.ResolveCommand(item.Path, "remote")));

    private static DiffSideJson? DiffSide(DiffSide? side)
    {
        if (side is null)
        {
            return null;
        }

        string? mtime = side.MtimeUtc is { } value ? Utc(value) : null;
        return new DiffSideJson(
            side.Type == EntryType.Directory ? "dir" : "file",
            side.Sha256?.Value,
            side.Size,
            mtime,
            side.Executable,
            side.Revision?.Value,
            side.UpdatedBy);
    }

    private static DiffSectionJson DiffSection(DiffSection section, bool statOnly)
    {
        DiffSectionJson empty = new(section.Title, section.OldLabel, section.NewLabel, "identical", null, null, null, null, null, null, null, null, null, null);
        return section.Outcome switch
        {
            TextOutcome text => empty with
            {
                Outcome = "text",
                Added = text.Added,
                Removed = text.Removed,
                Hunks = statOnly ? null : [.. text.Hunks.Select(Hunk)],
            },
            BinaryOutcome => empty with { Outcome = "binary" },
            TooLargeOutcome large => empty with { Outcome = "too-large", OldSize = large.OldSize, NewSize = large.NewSize },
            EolOnlyOutcome eol => empty with { Outcome = "eol-only", OldEol = Eol(eol.Old), NewEol = Eol(eol.New) },
            ExecOnlyOutcome exec => empty with { Outcome = "exec-only", OldExec = exec.OldExecutable, NewExec = exec.NewExecutable },
            TypeMismatchOutcome type => empty with { Outcome = "type-mismatch", Description = type.Description },
            _ => empty,
        };
    }

    private static HunkJson Hunk(Domain.Diffing.DiffHunk hunk) => new(
        hunk.OldStart,
        hunk.OldLines,
        hunk.NewStart,
        hunk.NewLines,
        [.. hunk.Lines.Select(line => new HunkLineJson(line.Op.ToString(), line.Text))],
        new NoNewlineJson(hunk.OldNoNewlineAtEnd, hunk.NewNoNewlineAtEnd));

    private static string Eol(Domain.Diffing.LineEndingStyle style) => style switch
    {
        Domain.Diffing.LineEndingStyle.CrLf => "crlf",
        Domain.Diffing.LineEndingStyle.Lf => "lf",
        Domain.Diffing.LineEndingStyle.Cr => "cr",
        Domain.Diffing.LineEndingStyle.Mixed => "mixed",
        _ => "none",
    };

    public static int Write(ShareListResponse listed, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(listed);

        Emit(
            new ShareListJson(
                "shares",
                [.. listed.Shares.Select(alias => new ShareJson(alias.Name.Value, alias.ShareId.ToString(), Utc(alias.CreatedUtc), alias.CreatedBy))],
                [.. listed.Warnings],
                exitCode),
            Context.ShareListJson);
        return exitCode;
    }

    public static int Write(RenameShareResponse renamed, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(renamed);

        Emit(
            new RenameJson("rename", renamed.ShareId.ToString(), renamed.Name.Value, [.. renamed.Previous.Select(name => name.Value)], renamed.Unchanged, exitCode),
            Context.RenameJson);
        return exitCode;
    }

    public static int Write(UnpublishReport report, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(report);

        Emit(
            new UnpublishJson("unpublish", report.ShareId.ToString(), report.PublishId.Value, report.Url, report.BlobsDeleted, exitCode),
            Context.UnpublishJson);
        return exitCode;
    }

    private static string CopyMethodName(CopyMethod method) => method switch
    {
        CopyMethod.PutBlobFromUrl => "put-blob-from-url",
        CopyMethod.BlocksFromUrl => "blocks-from-url",
        _ => "streamed",
    };

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

    public static string CauseCode(SkipCause cause) => SkipCauseCodes.Of(cause);

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

    private static TotalsJson Totals(TransferTotals totals, bool sent = false) => new(totals.Files, totals.Bytes, sent ? totals.StoredBytes : null);

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

/// <param name="StoredBytes">Uploads only: what went over the wire (less than <paramref name="Bytes"/> when sent compressed).</param>
internal sealed record TotalsJson(int Files, long Bytes, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StoredBytes = null);

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
    List<string> Names,
    string? RootPath,
    string Provider,
    string AccountName,
    string Container,
    long? BaseRevision,
    long RemoteRevision,
    string RemoteUpdatedBy,
    string RemoteUpdatedUtc,
    string CreatedUtc,
    int FileCount,
    long TotalBytes,
    int? OpenConflicts,
    string Compression,
    int ExitCode);

internal sealed record DeleteJson(
    string Command,
    string ShareId,
    long? MarkerRevision,
    string? DeletedUtc,
    string? DeletedBy,
    bool WasAlreadyDeleted,
    bool WasIncomplete,
    List<string> NamesFreed,
    int ObjectsDeleted,
    long BytesDeleted,
    long HistoryRevisionsSwept,
    bool LocalStateRemoved,
    int RecoverableCopies,
    int PublicationsRemoved,
    List<string> Warnings,
    int ExitCode);

internal sealed record PublishJson(
    string Command,
    string ShareId,
    string PublishId,
    string Url,
    long Revision,
    string Title,
    int Files,
    long Bytes,
    Dictionary<string, int> Copies,
    bool Verified,
    string? VerificationDetail,
    string ShellCommand,
    string PowerShellCommand,
    List<SkippedJson> Skipped,
    List<string> Warnings,
    int ExitCode);

internal sealed record PublicationJson(
    string Id,
    string Status,
    string Title,
    long Revision,
    int Files,
    long Bytes,
    string CreatedUtc,
    string CreatedBy,
    string PublishAccount,
    string Url,
    string SiteAssetsVersion);

internal sealed record PublicationsJson(string Command, string ShareId, List<PublicationJson> Publications, int ExitCode);

internal sealed record CompressionJson(string Command, string ShareId, string? Name, string Compression, long Revision, int FormatVersion, bool Changed, int ExitCode);

internal sealed record DiffJson(string Command, string ShareId, List<DiffItemJson> Items, int ExitCode);

internal sealed record DiffItemJson(
    string Path,
    string? Kind,
    DiffSideJson? Local,
    DiffSideJson? Remote,
    DiffSideJson? Base,
    List<DiffSectionJson> Sections,
    List<string> Notes,
    ResolveJson? Resolve);

internal sealed record DiffSideJson(
    string Type,
    string? Sha256,
    long? Size,
    string? MtimeUtc,
    bool Exec,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdatedBy);

internal sealed record DiffSectionJson(
    string Title,
    string OldLabel,
    string NewLabel,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Added,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Removed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<HunkJson>? Hunks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OldEol,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NewEol,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OldExec,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NewExec,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OldSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NewSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

internal sealed record HunkJson(int OldStart, int OldLines, int NewStart, int NewLines, List<HunkLineJson> Lines, NoNewlineJson NoNewlineAtEnd);

internal sealed record HunkLineJson(string Op, string Text);

internal sealed record NoNewlineJson(bool Old, bool New);

internal sealed record ResolveJson(string KeepLocal, string KeepRemote);

internal sealed record ShareJson(string Name, string ShareId, string CreatedUtc, string CreatedBy);

internal sealed record ShareListJson(string Command, List<ShareJson> Shares, List<string> Warnings, int ExitCode);

internal sealed record RenameJson(string Command, string ShareId, string Name, List<string> Previous, bool Unchanged, int ExitCode);

internal sealed record UnpublishJson(string Command, string ShareId, string PublishId, string Url, int BlobsDeleted, int ExitCode);

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
    string Name,
    int FileCount,
    long TotalBytes,
    TotalsJson Uploaded,
    List<SkippedJson> Skipped,
    List<string> Warnings,
    string CloneCommand,
    string Compression,
    int ExitCode);

/// <param name="ShareId"><c>null</c> only for a dry run of <c>--create</c>.</param>
internal sealed record PushJson(
    string Command,
    string? ShareId,
    string? ShareName,
    string Target,
    long? Revision,
    bool Committed,
    bool DryRun,
    bool Created,
    int Files,
    long Bytes,
    TotalsJson Uploaded,
    TotalsJson AlreadyStored,
    int CommitAttempts,
    List<SkippedJson> Skipped,
    List<string> Warnings,
    int IgnoredByShare,
    string Compression,
    PushFiltersJson? Filters,
    PushFilteredOutJson FilteredOut,
    int ExitCode);

/// <param name="UpdatedSince">The resolved cutoff (UTC, to the second), or <c>null</c>.</param>
/// <param name="UpdatedSinceSpec"><c>--updated-since</c> as typed, or <c>null</c>.</param>
internal sealed record PushFiltersJson(string? UpdatedSince, string? UpdatedSinceSpec, List<string> Include, List<string> Exclude, long? MaxSizeBytes);

internal sealed record PushFilteredOutJson(int Total, int NotChanged, int NotIncluded, int Excluded, int TooLarge);

internal sealed record PreviewJson(string Command, bool DryRun, string RootPath, int FileCount, long TotalBytes, bool IsUnsafeRoot, int ExitCode);

internal sealed record ErrorJson(
    string Code,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ErrorJson>? Details);

internal sealed record FailureJson(ErrorJson Error, int ExitCode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, IndentSize = 2, NewLine = "\n")]
[JsonSerializable(typeof(SyncJson))]
[JsonSerializable(typeof(CreateJson))]
[JsonSerializable(typeof(PushJson))]
[JsonSerializable(typeof(PreviewJson))]
[JsonSerializable(typeof(FailureJson))]
[JsonSerializable(typeof(ConflictListJson))]
[JsonSerializable(typeof(InfoJson))]
[JsonSerializable(typeof(GcJson))]
[JsonSerializable(typeof(DeleteJson))]
[JsonSerializable(typeof(UpdateJson))]
[JsonSerializable(typeof(PublishJson))]
[JsonSerializable(typeof(PublicationsJson))]
[JsonSerializable(typeof(UnpublishJson))]
[JsonSerializable(typeof(ShareListJson))]
[JsonSerializable(typeof(DiffJson))]
[JsonSerializable(typeof(CompressionJson))]
[JsonSerializable(typeof(RenameJson))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
