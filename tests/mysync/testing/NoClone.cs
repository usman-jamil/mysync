using MySync.Application.Abstractions.Composition;
using MySync.Application.Abstractions.FileSystem;
using MySync.Application.Shares.Clone;
using MySync.Application.Sync.Run;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// <c>mysync clone</c>'s folders, for test containers that aren't about it but validate the whole
/// service graph (the clone handler needs them to be constructed). Every call fails the test.
/// <c>DiskCloneTests</c> runs the real ones on disk.
/// </summary>
public sealed class NoCloneFolders : ICloneFolders
{
    public string CurrentFolder => throw Unused();

    public Result<CloneFolderProbe> Inspect(string fullPath) => throw Unused();

    public Result<IReadOnlyList<string>> Create(string fullPath) => throw Unused();

    public ILocalFolder Open(string fullPath) => throw Unused();

    public Result DeleteArtifact(string fullPath) => throw Unused();

    public Result<bool> DeleteFolderIfEmpty(string fullPath) => throw Unused();

    internal static NotSupportedException Unused() => new("mysync clone isn't composed in this test container.");
}

/// <summary>The clone runner for the same containers as <see cref="NoCloneFolders"/>.</summary>
public sealed class NoCloneRunner : ICloneRunner
{
    public Task<Result<SyncReport>> CloneAsync(string root, CloneShareCommand command, CancellationToken cancellationToken) => throw NoCloneFolders.Unused();
}
