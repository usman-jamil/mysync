using MySync.Application.Abstractions.FileSystem;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using MySync.Testing.Contracts;

namespace MySync.Infrastructure.UnitTests.FileSystem;

/// <summary>
/// The <see cref="ILocalFolder"/> contract against the real disk, on every CI OS. The same suite
/// runs against <see cref="InMemoryLocalFolder"/> in the application tests, so the pipeline tests
/// there can be trusted to mean what they say about a real folder.
/// </summary>
public sealed class DiskLocalFolderContractTests : LocalFolderContractTests, IDisposable
{
    private readonly List<TempShareFolder> _folders = [];

    protected override bool SupportsExecutableBit => !OperatingSystem.IsWindows();

    protected override Task<ILocalFolder> CreateFolderAsync()
    {
        TempShareFolder temp = new();
        _folders.Add(temp);
        return Task.FromResult<ILocalFolder>(new LocalFolder(new ShareRoot(temp.Path)));
    }

    protected override Task SeedFileAsync(ILocalFolder folder, string path, byte[] content)
    {
        string full = Full(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return File.WriteAllBytesAsync(full, content);
    }

    protected override Task SeedDirectoryAsync(ILocalFolder folder, string path)
    {
        Directory.CreateDirectory(Full(folder, path));
        return Task.CompletedTask;
    }

    protected override async Task<byte[]?> ReadFileAsync(ILocalFolder folder, string path)
    {
        string full = Full(folder, path);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full) : null;
    }

    protected override Task<bool> IsDirectoryAsync(ILocalFolder folder, string path) => Task.FromResult(Directory.Exists(Full(folder, path)));

    protected override Task<bool> IsExecutableAsync(ILocalFolder folder, string path) =>
        Task.FromResult(!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Full(folder, path)) & UnixFileMode.UserExecute) != 0);

    public void Dispose()
    {
        foreach (TempShareFolder folder in _folders)
        {
            folder.Dispose();
        }
    }

    private static string Full(ILocalFolder folder, string path) => Path.Combine(folder.RootPath, path.Replace('/', Path.DirectorySeparatorChar));
}
