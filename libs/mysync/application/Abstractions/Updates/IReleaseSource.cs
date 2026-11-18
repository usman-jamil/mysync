using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Application.Abstractions.Updates;

/// <summary>
/// Where mysync releases come from: GitHub releases (<c>MySync.Updates.GitHub</c>), or a local
/// folder laid out the same way (a mirror, or tests). Read-only; it never decides what to install.
/// </summary>
public interface IReleaseSource
{
    /// <summary>For messages, e.g. <c>GitHub usman-jamil/mysync</c>. Never a token.</summary>
    string Description { get; }

    /// <summary>The newest release; prereleases only when asked for. <c>Update.ReleaseNotFound</c> if there is none.</summary>
    Task<Result<ReleaseInfo>> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken);

    /// <summary>A specific release (for <c>--version</c>). <c>Update.ReleaseNotFound</c> if it doesn't exist.</summary>
    Task<Result<ReleaseInfo>> GetAsync(ReleaseVersion version, CancellationToken cancellationToken);

    /// <summary>Copies an asset of a release into <paramref name="destination"/>.</summary>
    Task<Result> DownloadAsync(ReleaseAsset asset, Stream destination, CancellationToken cancellationToken);
}

/// <param name="NotesUrl">Where a person can read what changed; <c>null</c> if the source has no notes.</param>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string? NotesUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Find(string name) => Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));
}

/// <param name="Location">Where the source finds it (a URL or a path); only the source interprets it.</param>
public sealed record ReleaseAsset(string Name, long Size, string Location);
