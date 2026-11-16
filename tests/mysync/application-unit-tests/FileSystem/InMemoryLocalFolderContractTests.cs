using MySync.Application.Abstractions.FileSystem;
using MySync.Testing;
using MySync.Testing.Contracts;

namespace MySync.Application.UnitTests.FileSystem;

/// <summary>Runs the shared <see cref="ILocalFolder"/> contract against the in-memory fake, as Unix.</summary>
public sealed class InMemoryLocalFolderContractTests : LocalFolderContractTests
{
    protected override bool SupportsExecutableBit => true;

    protected override Task<ILocalFolder> CreateFolderAsync() => Task.FromResult<ILocalFolder>(new InMemoryLocalFolder());

    protected override Task SeedFileAsync(ILocalFolder folder, string path, byte[] content)
    {
        Fake(folder).WriteFile(path, content);
        return Task.CompletedTask;
    }

    protected override Task SeedDirectoryAsync(ILocalFolder folder, string path)
    {
        Fake(folder).AddDirectory(path);
        return Task.CompletedTask;
    }

    protected override Task<byte[]?> ReadFileAsync(ILocalFolder folder, string path) =>
        Task.FromResult(Fake(folder).Exists(path) && !Fake(folder).IsDirectory(path) ? Fake(folder).ReadBytes(path) : null);

    protected override Task<bool> IsDirectoryAsync(ILocalFolder folder, string path) => Task.FromResult(Fake(folder).IsDirectory(path));

    protected override Task<bool> IsExecutableAsync(ILocalFolder folder, string path) => Task.FromResult(Fake(folder).IsExecutable(path));

    internal static InMemoryLocalFolder Fake(ILocalFolder folder) => (InMemoryLocalFolder)folder;
}

/// <summary>The same contract with the fake simulating Windows (no executable bit).</summary>
public sealed class InMemoryWindowsLocalFolderContractTests : LocalFolderContractTests
{
    protected override bool SupportsExecutableBit => false;

    protected override Task<ILocalFolder> CreateFolderAsync() =>
        Task.FromResult<ILocalFolder>(new InMemoryLocalFolder { ExecutableBitSupported = false });

    protected override Task SeedFileAsync(ILocalFolder folder, string path, byte[] content)
    {
        InMemoryLocalFolderContractTests.Fake(folder).WriteFile(path, content);
        return Task.CompletedTask;
    }

    protected override Task SeedDirectoryAsync(ILocalFolder folder, string path)
    {
        InMemoryLocalFolderContractTests.Fake(folder).AddDirectory(path);
        return Task.CompletedTask;
    }

    protected override Task<byte[]?> ReadFileAsync(ILocalFolder folder, string path)
    {
        InMemoryLocalFolder fake = InMemoryLocalFolderContractTests.Fake(folder);
        return Task.FromResult(fake.Exists(path) && !fake.IsDirectory(path) ? fake.ReadBytes(path) : null);
    }

    protected override Task<bool> IsDirectoryAsync(ILocalFolder folder, string path) =>
        Task.FromResult(InMemoryLocalFolderContractTests.Fake(folder).IsDirectory(path));

    protected override Task<bool> IsExecutableAsync(ILocalFolder folder, string path) =>
        Task.FromResult(InMemoryLocalFolderContractTests.Fake(folder).IsExecutable(path));
}
