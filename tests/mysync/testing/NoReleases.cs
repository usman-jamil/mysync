using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// A release source with nothing in it, for test containers that aren't about <c>mysync update</c>
/// but validate the whole service graph (the update handler needs a source to be constructed).
/// </summary>
public sealed class NoReleases : IReleaseSource
{
    public string Description => "no releases";

    public Task<Result<ReleaseInfo>> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound("in a test")));

    public Task<Result<ReleaseInfo>> GetAsync(ReleaseVersion version, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound("in a test")));

    public Task<Result> DownloadAsync(ReleaseAsset asset, Stream destination, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(UpdateErrors.ReleaseNotFound("in a test")));
}

/// <summary>An installer for test containers: describes a binary that can't be replaced.</summary>
public sealed class NotInstalled : IExecutableInstaller
{
    public InstalledExecutable Locate() =>
        new("/test/mysync", "linux-x64", ReleaseVersion.TryParse("0.0.0", out ReleaseVersion? version) ? version : null!, "a test");

    public Result CheckReplaceable(InstalledExecutable installed) => Result.Failure(UpdateErrors.NotInstalledBinary("a test"));

    public Task<Result<IUpdateWorkspace>> CreateWorkspaceAsync(InstalledExecutable installed, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<IUpdateWorkspace>(UpdateErrors.NotInstalledBinary("a test")));
}
