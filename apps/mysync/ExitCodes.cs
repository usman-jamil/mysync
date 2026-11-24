using SharedKernel;

namespace MySync.Cli;

/// <summary>
/// Process exit codes (CLAUDE.md §10). Nothing below <c>apps/</c> knows about these; failures
/// are mapped from <see cref="Error.Code"/>, partial successes from the report.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int UsageOrConfiguration = 2;
    public const int ConflictsOpen = 3;
    public const int FolderPrecondition = 4;
    public const int CommitRetriesExhausted = 5;
    public const int FolderLocked = 6;
    public const int MassDeleteBlocked = 7;
    public const int NotFoundOrUnauthorized = 8;
    public const int PathsSkipped = 9;
    public const int PublicationNotVerified = 10;
    public const int Cancelled = 130;

    /// <summary>A publication that is up but couldn't be seen anonymously: exit 10.</summary>
    public static int FromReport(Application.Publishing.Publish.PublishReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Verified ? Success : PublicationNotVerified;
    }

    public static int FromError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is ValidationError || error.Type == ErrorType.Validation)
        {
            return UsageOrConfiguration;
        }

        return error.Code switch
        {
            _ when error.Code.StartsWith("Configuration.", StringComparison.Ordinal) => UsageOrConfiguration,
            "Storage.ClockSkew" => UsageOrConfiguration, // this machine's setup, like a configuration error
            "Shares.FolderNotEmpty" or "Shares.AlreadyAShare" or "Shares.NotAShare" or "Shares.UnsafeRoot" or "Shares.NameTaken" => FolderPrecondition,
            "Push.SourceIsTheShare" or "Push.TargetIsAFile" => FolderPrecondition,
            "Pull.DestinationOccupied" or "Pull.DestinationIsTheShare" => FolderPrecondition,
            "Clone.TargetIsAFile" or "Clone.InsideAShare" or "Clone.CannotCreate" => FolderPrecondition,
            "Pull.NoPushes" => NotFoundOrUnauthorized,
            "Sync.CommitRetriesExhausted" or "Sync.ShareChangedDuringGc" => CommitRetriesExhausted,
            "State.FolderLocked" or "Tui.AlreadyRunning" => FolderLocked,
            "Sync.MassDeleteBlocked" => MassDeleteBlocked,
            "Shares.NotFound" or "Shares.Deleted" or "Diff.VersionNotStored" or "Storage.UnsupportedEncoding" or "Shares.NameNotFound" or "Shares.Incomplete" or "Storage.Unauthorized" => NotFoundOrUnauthorized,
            "Update.ReleaseNotFound" or "Update.NoBuildForPlatform" => NotFoundOrUnauthorized,
            "Update.NotWritable" or "Update.NotInstalledBinary" => FolderPrecondition,
            "Publish.NotFound" or "Publish.RevisionNotFound" or "Publish.Unauthorized" or "Publish.CannotReadServiceProperties" => NotFoundOrUnauthorized,
            "Publish.RegistryRetriesExhausted" => CommitRetriesExhausted,
            _ => Unexpected,
        };
    }
}
