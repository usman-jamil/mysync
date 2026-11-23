using MySync.Application.Abstractions.FileSystem;
using MySync.Domain.State;

namespace MySync.Infrastructure.FileSystem;

/// <summary>
/// <c>mysync pull</c>'s destination: an ordinary <see cref="LocalFolder"/> rooted where the user
/// asked, with the same <see cref="PathResolver"/> rules, staging in <c>.mysync-pull.tmp/</c>.
/// Relative paths are taken from the current folder (<paramref name="current"/>).
/// </summary>
internal sealed class LocalFolderFactory(ShareRoot current) : ILocalFolderFactory
{
    public ILocalFolder OpenPullDestination(string? path)
    {
        string root = string.IsNullOrEmpty(path) ? current.FullPath : Path.GetFullPath(path, current.FullPath);
        return new LocalFolder(new ShareRoot(root), StateFileNames.PullStagingDirectory, probeByWriting: false);
    }
}
