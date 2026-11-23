using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Pull;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Sync;

/// <summary>
/// <c>mysync pull</c> onto a real disk (CLAUDE.md §10c): the real destination folder, its
/// staging, the path safety rules and the same-share refusal; only storage is in memory.
/// </summary>
public sealed class DiskPullTests : IDisposable
{
    private static readonly DateTime OldMtime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryAliasStore _aliases = new();
    private readonly InMemoryShareStore _store = new();
    private readonly TempShareFolder _laptop = new();
    private readonly TempShareFolder _collector = new();
    private readonly TempShareFolder _outside = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShareReference Team => ShareReference.Parse("team").Value;

    private async Task CreateShareAsync()
    {
        _laptop.WriteFile("docs/readme.md", "# readme", OldMtime)
            .WriteFile("scripts/deploy.sh", "#!/bin/sh\necho hi\n", OldMtime, executable: true)
            .CreateDirectory("empty");
        Ok(await Send<CreateShareCommand, CreateShareResponse>(_laptop, new CreateShareCommand(ShareName.Create("team").Value)));
    }

    [Fact]
    public async Task APull_Should_WriteTheFilesWithTheirMtimeAndMode_AndLeaveNoStagingOrState()
    {
        await CreateShareAsync();

        PullReport report = Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [])));

        report.Destination.ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_collector.Path)));
        _collector.Files().ShouldBe(new Dictionary<string, string>
        {
            ["docs/readme.md"] = "# readme",
            ["scripts/deploy.sh"] = "#!/bin/sh\necho hi\n",
        });
        File.GetLastWriteTimeUtc(_collector.Combine("docs/readme.md")).ShouldBe(OldMtime);
        _collector.IsExecutable("scripts/deploy.sh").ShouldBe(!OperatingSystem.IsWindows());
        Directory.Exists(_collector.Combine("empty")).ShouldBeTrue();
        Directory.Exists(_collector.Combine(StateFileNames.PullStagingDirectory)).ShouldBeFalse();
        _collector.Exists(StateFileNames.State).ShouldBeFalse();
        _collector.Exists(StateFileNames.Lock).ShouldBeFalse();
    }

    [Fact]
    public async Task To_Should_BeTakenFromTheCurrentFolder_AndCreated()
    {
        await CreateShareAsync();

        PullReport report = Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, ["docs"], To: Path.Combine("out", "sub"))));

        report.Destination.ShouldBe(_collector.Combine(Path.Combine("out", "sub")));
        _collector.Files().Keys.ShouldBe(["out/sub/docs/readme.md"]);
    }

    [Fact]
    public async Task ARerun_Should_FindEverythingAlreadyThere_ByContent()
    {
        await CreateShareAsync();
        Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [])));
        File.SetLastWriteTimeUtc(_collector.Combine("docs/readme.md"), DateTime.UtcNow); // same content, another mtime

        PullReport again = Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [])));

        again.Downloaded.Files.ShouldBe(0);
        again.AlreadyThere.Files.ShouldBe(2);
    }

    [Fact]
    public async Task DryRun_Should_WriteNothing_NotEvenTheFolderOrItsStaging()
    {
        await CreateShareAsync();
        _collector.WriteFile("existing/keep.txt", "k");

        Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [], To: "new", DryRun: true)));
        Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [], To: "existing", DryRun: true)));

        _collector.Exists("new").ShouldBeFalse();
        Directory.EnumerateFileSystemEntries(_collector.Combine("existing")).Select(Path.GetFileName).ShouldBe(["keep.txt"]);
    }

    [Fact]
    public void TheReadOnlyCaseProbe_Should_AgreeWithTheWritingOne()
    {
        _collector.CreateDirectory("Probe-Folder");
        ShareRoot root = new(_collector.Combine("Probe-Folder"));

        bool looked = new LocalFolder(root, StateFileNames.PullStagingDirectory, probeByWriting: false).IsCaseSensitive;
        bool written = new LocalFolder(root).IsCaseSensitive;

        looked.ShouldBe(written);
        new LocalFolder(new ShareRoot(_collector.Combine("not/there/yet")), StateFileNames.PullStagingDirectory, probeByWriting: false).IsCaseSensitive.ShouldBe(written);
        _collector.Exists("not").ShouldBeFalse();
    }

    [Fact]
    public async Task ALeftoverStagingFolder_Should_BeCleanedAway()
    {
        await CreateShareAsync();
        _collector.WriteFile($"{StateFileNames.PullStagingDirectory}/abandoned.part", "half a file");

        Ok(await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [])));

        Directory.Exists(_collector.Combine(StateFileNames.PullStagingDirectory)).ShouldBeFalse();
    }

    [Fact]
    public async Task ASymlinkInTheDestination_Should_StopThePull_AndNothingIsWrittenOutsideIt()
    {
        await CreateShareAsync();
        try
        {
            Directory.CreateSymbolicLink(_collector.Combine("docs"), _outside.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symlinks need Developer Mode on Windows: {exception.Message}");
        }

        Error error = (await Send<PullCommand, PullReport>(_collector, new PullCommand(Team, [], Overwrite: true))).Error;

        error.Code.ShouldBe("Pull.DestinationOccupied");
        error.Description.ShouldContain("'docs'");
        Directory.EnumerateFileSystemEntries(_outside.Path).ShouldBeEmpty();
        _collector.Exists("scripts/deploy.sh").ShouldBeFalse();
    }

    [Fact]
    public async Task PullingIntoAFolderOfTheSameShare_Should_BeRefused_AlsoFromBelowIt()
    {
        await CreateShareAsync();

        Error intoIt = (await Send<PullCommand, PullReport>(_laptop, new PullCommand(Team, []))).Error;
        Error below = (await Send<PullCommand, PullReport>(_laptop, new PullCommand(Team, [], To: "copy"))).Error;

        intoIt.Code.ShouldBe("Pull.DestinationIsTheShare");
        below.Code.ShouldBe("Pull.DestinationIsTheShare");
        _laptop.Exists("copy").ShouldBeFalse();
    }

    public void Dispose()
    {
        _laptop.Dispose();
        _collector.Dispose();
        _outside.Dispose();
    }

    private async Task<Result<TResponse>> Send<TCommand, TResponse>(TempShareFolder folder, TCommand command)
        where TCommand : ICommand<TResponse>
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new ShareRoot(folder.Path));
        services.AddSingleton<IShareStore>(_store);
        services.AddSingleton<IAliasStore>(_aliases);
        services.AddSingleton<TimeProvider>(new InstantTimeProvider());
        services.AddSingleton<MySync.Application.Abstractions.Updates.IReleaseSource>(new NoReleases());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.ISiteAssetSource>(new InMemorySiteAssetSource());
        services.AddSingleton<MySync.Application.Abstractions.Publishing.IPublishTarget>(provider =>
            new InMemoryPublishTarget((InMemoryShareStore)provider.GetRequiredService<IShareStore>()));
        services.AddApplication();
        services.AddInfrastructure();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, Ct);
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }
}
