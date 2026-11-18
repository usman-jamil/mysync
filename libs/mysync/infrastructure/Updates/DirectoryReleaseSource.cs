using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Infrastructure.Updates;

/// <summary>
/// Releases in a local folder, one subfolder per tag with the release's files in it, as
/// <c>gh release download</c> lays them out: <c>&lt;root&gt;/mysync-1.2.3/mysync-1.2.3-osx-arm64.tar.gz</c>,
/// <c>SHA256SUMS.txt</c>, <c>SHA256SUMS.txt.sig</c>. For an internal mirror, an offline machine, or
/// the update end-to-end test. Files are verified exactly as from GitHub.
/// </summary>
internal sealed class DirectoryReleaseSource(string root) : IReleaseSource
{
    public string Description => root;

    public Task<Result<ReleaseInfo>> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReleaseInfo? latest = Releases()
            .Where(release => includePrereleases || !release.Version.IsPrerelease)
            .OrderDescending(Comparer<ReleaseInfo>.Create((left, right) => left.Version.CompareTo(right.Version)))
            .FirstOrDefault();

        return Task.FromResult(latest is null
            ? Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound($"in {root}"))
            : Result.Success(latest));
    }

    public Task<Result<ReleaseInfo>> GetAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        string folder = Path.Combine(root, ReleaseAssets.Tag(version));
        return Task.FromResult(Directory.Exists(folder)
            ? Result.Success(Describe(folder, version))
            : Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound($"{version} in {root}")));
    }

    public async Task<Result> DownloadAsync(ReleaseAsset asset, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        try
        {
            await using FileStream file = File.OpenRead(asset.Location);
            await file.CopyToAsync(destination, cancellationToken);
            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(UpdateErrors.SourceUnreachable(exception.Message));
        }
    }

    private IEnumerable<ReleaseInfo> Releases()
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(ReleaseAssets.TagPrefix, StringComparison.Ordinal) && ReleaseVersion.TryParse(name, out ReleaseVersion? version))
            {
                yield return Describe(folder, version);
            }
        }
    }

    private static ReleaseInfo Describe(string folder, ReleaseVersion version) => new(
        version,
        ReleaseAssets.Tag(version),
        null,
        [.. Directory.EnumerateFiles(folder).Select(file => new ReleaseAsset(Path.GetFileName(file), new FileInfo(file).Length, file))]);
}
