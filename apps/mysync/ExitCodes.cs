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
    public const int Cancelled = 130;

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
            "Shares.FolderNotEmpty" or "Shares.AlreadyAShare" or "Shares.UnsafeRoot" => FolderPrecondition,
            "Sync.CommitRetriesExhausted" => CommitRetriesExhausted,
            "State.FolderLocked" => FolderLocked,
            "Sync.MassDeleteBlocked" => MassDeleteBlocked,
            "Shares.NotFound" or "Storage.Unauthorized" => NotFoundOrUnauthorized,
            _ => Unexpected,
        };
    }
}
