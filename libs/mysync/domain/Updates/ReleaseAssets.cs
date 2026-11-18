using MySync.Domain.Content;

namespace MySync.Domain.Updates;

/// <summary>
/// The one place release names are composed (CLAUDE.md §16, §19): tags, archive names,
/// <c>SHA256SUMS.txt</c> and its signature. The release workflow and the install scripts build the
/// same names; <c>ReleaseAssetsTests</c> pins them, and changing them breaks <c>mysync update</c> for
/// every installed copy.
/// </summary>
public static class ReleaseAssets
{
    public const string TagPrefix = "mysync-";

    /// <summary>Where releases are published unless <c>MYSYNC_UPDATE_SOURCE</c> says otherwise.</summary>
    public const string DefaultRepository = "usman-jamil/mysync";

    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>ECDSA P-256 / SHA-256 signature of <see cref="ChecksumsName"/>, DER-encoded (what <c>openssl dgst -sign</c> writes).</summary>
    public const string SignatureName = "SHA256SUMS.txt.sig";

    /// <summary>The seven targets the release workflow publishes.</summary>
    public static IReadOnlyList<string> RuntimeIdentifiers { get; } =
        ["linux-x64", "linux-arm64", "linux-musl-x64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];

    public static string Tag(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return TagPrefix + version;
    }

    /// <summary><c>mysync-1.2.3-linux-x64.tar.gz</c>; <c>.zip</c> for Windows.</summary>
    public static string ArchiveName(ReleaseVersion version, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        return $"{TagPrefix}{version}-{runtimeIdentifier}.{(IsWindows(runtimeIdentifier) ? "zip" : "tar.gz")}";
    }

    /// <summary>The executable inside the archive.</summary>
    public static string ExecutableName(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        return IsWindows(runtimeIdentifier) ? "mysync.exe" : "mysync";
    }

    public static bool IsWindows(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>sha256sum</c> output (<c>&lt;hex&gt;  &lt;name&gt;</c>, optionally <c>*name</c> for
    /// binary mode). Malformed lines are skipped; the caller then finds its archive missing.
    /// </summary>
    public static IReadOnlyDictionary<string, ContentHash> ParseChecksums(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, ContentHash> sums = new(StringComparer.Ordinal);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0 || !ContentHash.TryParse(line[..space], out ContentHash? hash))
            {
                continue;
            }

            string name = line[space..].TrimStart(' ').TrimStart('*');
            if (name.Length > 0)
            {
                sums[name] = hash;
            }
        }

        return sums;
    }
}
