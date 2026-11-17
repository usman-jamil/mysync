using System.Globalization;
using System.Text;
using MySync.Application.Abstractions.Logging;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Infrastructure.Logging;

/// <summary>
/// <c>.mysynctool.log</c> (CLAUDE.md §7): one line per conflict detected or resolved, e.g.
/// <c>2026-11-16T09:02:55Z CONFLICT modify/modify reports/q3.xlsx local=bb22… remote=cc33… (rev 18, ali@SERVER01)</c>.
/// Rotated once it would pass <see cref="DefaultMaxBytes"/>, keeping one <c>.mysynctool.log.1</c>.
/// </summary>
/// <remarks>
/// <c>updatedBy</c> comes from a shared manifest, so it is written through <see cref="PathErrors.Display"/>
/// like the path: a crafted value can't start a fake line or send escape sequences to whoever runs
/// <c>cat</c> on the log. Hashes are shortened to 12 characters.
/// </remarks>
internal sealed class FileConflictLog(ShareRoot root, long maxBytes) : IConflictLog
{
    internal const long DefaultMaxBytes = 5 * 1024 * 1024;

    private const int HashLength = 12;

    private readonly Lock _gate = new();

    public FileConflictLog(ShareRoot root)
        : this(root, DefaultMaxBytes)
    {
    }

    public Task<Result> RecordDetectedAsync(Conflict conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{Timestamp(conflict.LastSeenUtc)} CONFLICT {conflict.Kind.ToCode()} {PathErrors.Display(conflict.Path.Value)} local={Side(conflict.Local)} remote={Side(conflict.Remote)} (rev {conflict.RemoteRevision}, {PathErrors.Display(conflict.RemoteUpdatedBy)})");

        return AppendAsync(line, cancellationToken);
    }

    public Task<Result> RecordResolvedAsync(SharePath path, KeepSide kept, DateTimeOffset resolvedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        string keep = kept == KeepSide.Local ? "keep-local" : "keep-remote";
        return AppendAsync($"{Timestamp(resolvedUtc)} RESOLVED {keep} {PathErrors.Display(path.Value)}", cancellationToken);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    private static string Side(ManifestEntry? entry) => entry switch
    {
        null => "deleted",
        DirectoryEntry => "dir",
        FileEntry file => $"{file.Sha256.Value[..HashLength]}…",
        _ => "?",
    };

    // Lines are short and rare: a synchronous append under a lock keeps them whole and in order.
    private Task<Result> AppendAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        string log = root.Combine(StateFileNames.ConflictLog);

        lock (_gate)
        {
            try
            {
                FileInfo current = new(log);
                if (current.Exists && current.Length + bytes.Length > maxBytes)
                {
                    File.Move(log, root.Combine(StateFileNames.RotatedConflictLog), overwrite: true);
                }

                using FileStream stream = new(log, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                return Task.FromResult(Result.Success());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(Result.Failure(LocalFileErrors.WriteFailed(StateFileNames.ConflictLog, exception.Message)));
            }
        }
    }
}
