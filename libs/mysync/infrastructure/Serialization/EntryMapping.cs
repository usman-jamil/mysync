using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Infrastructure.Serialization;

/// <summary>
/// <see cref="ManifestEntry"/> ↔ <see cref="EntryJson"/>, shared by the manifest and the state
/// file so both spell an entry the same way. Reading validates through the Domain; a failure
/// comes back as a plain reason, which each caller wraps in its own error.
/// </summary>
internal static class EntryMapping
{
    public const string FileType = "file";
    public const string DirectoryType = "dir";

    public static EntryJson ToJson(ManifestEntry entry) => entry switch
    {
        FileEntry file => new EntryJson(FileType, file.Sha256.Value, file.Size, file.MtimeUtc, file.Executable),
        DirectoryEntry => new EntryJson(DirectoryType),
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown entry type."),
    };

    public static Dictionary<string, EntryJson> ToJson(IEnumerable<KeyValuePair<SharePath, ManifestEntry>> entries) =>
        entries.ToDictionary(pair => pair.Key.Value, pair => ToJson(pair.Value), StringComparer.Ordinal);

    public static Result<ManifestEntry> ToDomain(EntryJson json, string where, Func<string, Error> error)
    {
        ArgumentNullException.ThrowIfNull(json);

        switch (json.Type)
        {
            case DirectoryType:
                return Result.Success<ManifestEntry>(DirectoryEntry.Instance);

            case FileType:
                if (json.Sha256 is null || json.Size is null || json.MtimeUtc is null || json.Exec is null)
                {
                    return Result.Failure<ManifestEntry>(error($"{where} is a file without sha256, size, mtimeUtc and exec"));
                }

                if (!ContentHash.TryParse(json.Sha256, out ContentHash? hash))
                {
                    return Result.Failure<ManifestEntry>(error($"{where} has an invalid sha256"));
                }

                Result<FileEntry> file = FileEntry.Create(hash, json.Size.Value, json.MtimeUtc.Value, json.Exec.Value);
                return file.IsSuccess
                    ? Result.Success<ManifestEntry>(file.Value)
                    : Result.Failure<ManifestEntry>(error($"{where}: {file.Error.Description}"));

            default:
                return Result.Failure<ManifestEntry>(error($"{where} has unknown type '{PathErrors.Display(json.Type)}'"));
        }
    }

    /// <summary>Validates every key as a <see cref="SharePath"/> and every value as an entry.</summary>
    public static Result<List<KeyValuePair<SharePath, ManifestEntry>>> ToDomain(
        Dictionary<string, EntryJson> entries, Func<string, Error> error)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<KeyValuePair<SharePath, ManifestEntry>> result = new(entries.Count);

        foreach ((string key, EntryJson json) in entries)
        {
            Result<SharePath> path = SharePath.Create(key);
            if (path.IsFailure)
            {
                return Result.Failure<List<KeyValuePair<SharePath, ManifestEntry>>>(error(path.Error.Description));
            }

            Result<ManifestEntry> entry = ToDomain(json, $"'{PathErrors.Display(key)}'", error);
            if (entry.IsFailure)
            {
                return Result.Failure<List<KeyValuePair<SharePath, ManifestEntry>>>(entry.Error);
            }

            result.Add(new KeyValuePair<SharePath, ManifestEntry>(path.Value, entry.Value));
        }

        return result;
    }
}
