using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySync.Application.Abstractions.Hashing;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Updates;
using MySync.Application.Maintenance.Update;
using MySync.Domain.Updates;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Maintenance;

/// <summary>
/// <c>mysync update</c>: nothing is installed unless the release is signed, the archive matches the
/// signed checksum, and the new binary runs and passes self-check.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private const string Rid = "osx-arm64";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeReleaseSource _source = new();
    private readonly FakeInstaller _installer = new(Rid, V("0.3.0"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUpdate_Should_VerifyTestAndInstallTheNewBinary()
    {
        _source.Publish(Release("0.4.0"));

        UpdateReport report = Ok(await UpdateAsync());

        report.Status.ShouldBe(UpdateStatus.Updated);
        report.Current.ToString().ShouldBe("0.3.0");
        report.Target.ToString().ShouldBe("0.4.0");
        report.SignificantChange.ShouldBeTrue();
        _installer.Workspace!.Ran.ShouldBe(["--version", "self-check"]);
        _installer.Workspace.Installed.ShouldBe("new mysync 0.4.0");
        _installer.Workspace.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AlreadyTheLatest_Should_DoNothing()
    {
        _source.Publish(Release("0.3.0"));
        _source.Publish(Release("0.2.0"));

        UpdateReport report = Ok(await UpdateAsync());

        report.Status.ShouldBe(UpdateStatus.UpToDate);
        _installer.Workspace.ShouldBeNull();
        _source.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALatestOlderThanThisBinary_Should_NeverDowngrade_WithoutAnExplicitVersion()
    {
        _source.Publish(Release("0.2.0"));

        Ok(await UpdateAsync()).Status.ShouldBe(UpdateStatus.UpToDate);

        _source.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_Should_ReportWithoutDownloading()
    {
        _source.Publish(Release("0.3.1"));

        UpdateReport report = Ok(await UpdateAsync(new UpdateCommand(CheckOnly: true)));

        report.Status.ShouldBe(UpdateStatus.Available);
        report.SignificantChange.ShouldBeFalse();
        _source.Downloads.ShouldBeEmpty();
        _installer.Workspace.ShouldBeNull();
    }

    [Fact]
    public async Task Prereleases_Should_BeLeftOut_UnlessAskedFor()
    {
        _source.Publish(Release("0.3.1"));
        _source.Publish(Release("0.4.0-beta.1"));

        Ok(await UpdateAsync(new UpdateCommand(CheckOnly: true))).Target.ToString().ShouldBe("0.3.1");
        Ok(await UpdateAsync(new UpdateCommand(CheckOnly: true, IncludePrereleases: true))).Target.ToString().ShouldBe("0.4.0-beta.1");
    }

    [Fact]
    public async Task AnExplicitVersion_Should_BeInstalled_EvenAnOlderOne()
    {
        _source.Publish(Release("0.2.0"));
        _source.Publish(Release("0.4.0"));

        UpdateReport report = Ok(await UpdateAsync(new UpdateCommand(Version: "0.2.0")));

        report.Status.ShouldBe(UpdateStatus.Updated);
        report.Downgrade.ShouldBeTrue();
        _installer.Workspace!.Installed.ShouldBe("new mysync 0.2.0");
    }

    [Fact]
    public async Task ATamperedArchive_Should_BeRefused_AndNothingInstalled()
    {
        _source.Publish(Release("0.4.0", archive: Encoding.UTF8.GetBytes("someone else's binary")));

        Error error = Fail(await UpdateAsync());

        error.Code.ShouldBe("Update.ChecksumMismatch");
        _installer.Workspace!.Installed.ShouldBeNull();
        _installer.Workspace.Ran.ShouldBeEmpty();
        _installer.Workspace.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ChecksumsSignedWithAnotherKey_Should_BeRefused_BeforeTheArchiveIsDownloaded()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _source.Publish(Release("0.4.0", signer: other));

        Fail(await UpdateAsync()).ShouldBe(UpdateErrors.SignatureInvalid);

        _source.Downloads.ShouldNotContain($"mysync-0.4.0-{Rid}.tar.gz");
        _installer.Workspace!.Installed.ShouldBeNull();
    }

    [Fact]
    public async Task AnUnsignedRelease_Should_BeRefused()
    {
        _source.Publish(Release("0.4.0", signed: false));

        Fail(await UpdateAsync()).Code.ShouldBe("Update.SignatureMissing");
    }

    [Fact]
    public async Task NoBuildForThisPlatform_Should_SaySo()
    {
        _source.Publish(Release("0.4.0", rid: "linux-x64"));

        Fail(await UpdateAsync()).ShouldBe(UpdateErrors.NoBuildForPlatform(V("0.4.0"), Rid));
    }

    [Theory]
    [InlineData("0.4.1", 0, 0, "reports version '0.4.1'")]
    [InlineData("0.4.0", 1, 0, "reports version")]
    [InlineData("0.4.0", 0, 1, "self-check exited with 1")]
    public async Task ANewBinaryThatFailsItsChecks_Should_NotBeInstalled(string reports, int versionExit, int selfCheckExit, string detail)
    {
        _source.Publish(Release("0.4.0"));
        _installer.Outputs = new()
        {
            ["--version"] = new ProcessOutcome(versionExit, reports + "\n"),
            ["self-check"] = new ProcessOutcome(selfCheckExit, "checks"),
        };

        Error error = Fail(await UpdateAsync());

        error.Code.ShouldBe("Update.NewBinaryFailed");
        error.Description.ShouldContain(detail);
        _installer.Workspace!.Installed.ShouldBeNull();
    }

    [Fact]
    public async Task AFolderThisUserCantWrite_Should_StopBeforeAnythingIsDownloaded()
    {
        _source.Publish(Release("0.4.0"));
        _installer.Replaceable = UpdateErrors.NotWritable("/usr/local/bin/mysync", "run 'sudo mysync update'");

        Fail(await UpdateAsync()).Code.ShouldBe("Update.NotWritable");

        _source.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADevelopmentBuild_Should_CheckButNotInstall()
    {
        _source.Publish(Release("0.4.0"));
        _installer.CannotReplaceBecause = "it runs as 'dotnet mysync.dll'";

        Ok(await UpdateAsync(new UpdateCommand(CheckOnly: true))).Status.ShouldBe(UpdateStatus.Available);
        Fail(await UpdateAsync()).Code.ShouldBe("Update.NotInstalledBinary");
        _source.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_Should_CountTheArchiveBytes()
    {
        _source.Publish(Release("0.4.0"));
        RecordingSyncProgressReporter progress = new();

        Ok(await UpdateAsync(new UpdateCommand(Progress: progress)));

        progress.Lines[0].ShouldBe("phase Downloading");
        progress.Lines[1].ShouldBe($"planned Download 1 files {Encoding.UTF8.GetByteCount("new mysync 0.4.0")} bytes");
        progress.Lines.ShouldContain(line => line.StartsWith("bytes Download", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("latest")]
    public async Task AnInvalidVersion_Should_BeAUsageError(string version)
    {
        Fail(await UpdateAsync(new UpdateCommand(Version: version))).ShouldBeOfType<ValidationError>().Errors.Single().Code.ShouldBe("Update.InvalidVersion");
    }

    public void Dispose() => _key.Dispose();

    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out ReleaseVersion? version) ? version : throw new ArgumentException(text);

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    private static Error Fail<T>(Result<T> result)
    {
        result.IsFailure.ShouldBeTrue("expected a failure");
        return result.Error;
    }

    // A release as the workflow publishes it: the archive, SHA256SUMS.txt, and its signature.
    private FakeRelease Release(string version, byte[]? archive = null, bool signed = true, ECDsa? signer = null, string rid = Rid)
    {
        ReleaseVersion v = V(version);
        string archiveName = ReleaseAssets.ArchiveName(v, rid);
        byte[] genuine = Encoding.UTF8.GetBytes($"new mysync {version}");
        byte[] sums = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(genuine))}  {archiveName}\n");
        Dictionary<string, byte[]> files = new()
        {
            [archiveName] = archive ?? genuine,
            [ReleaseAssets.ChecksumsName] = sums,
        };
        if (signed)
        {
            files[ReleaseAssets.SignatureName] = (signer ?? _key).SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        return new FakeRelease(v, files);
    }

    private async Task<Result<UpdateReport>> UpdateAsync(UpdateCommand? command = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IReleaseSource>(_source);
        services.AddSingleton<IExecutableInstaller>(_installer);
        services.AddSingleton<IContentHasher>(new TestContentHasher());
        services.AddSingleton(new UpdateOptions(_key.ExportSubjectPublicKeyInfoPem()));
        services.AddSingleton<MySync.Application.Abstractions.FileSystem.ICloneFolders>(new NoCloneFolders());
        services.AddSingleton<MySync.Application.Abstractions.Composition.ICloneRunner>(new NoCloneRunner());
        services.AddApplication();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<UpdateCommand, UpdateReport>>().Handle(command ?? new UpdateCommand(), Ct);
    }

    private sealed record FakeRelease(ReleaseVersion Version, Dictionary<string, byte[]> Files);

    private sealed class FakeReleaseSource : IReleaseSource
    {
        private readonly List<FakeRelease> _releases = [];

        public List<string> Downloads { get; } = [];

        public string Description => "fake";

        public void Publish(FakeRelease release) => _releases.Add(release);

        public Task<Result<ReleaseInfo>> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken)
        {
            FakeRelease? latest = _releases.Where(release => includePrereleases || !release.Version.IsPrerelease).MaxBy(release => release.Version);
            return Task.FromResult(latest is null ? Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound("anywhere")) : Result.Success(Info(latest)));
        }

        public Task<Result<ReleaseInfo>> GetAsync(ReleaseVersion version, CancellationToken cancellationToken)
        {
            FakeRelease? found = _releases.FirstOrDefault(release => release.Version.CompareTo(version) == 0);
            return Task.FromResult(found is null ? Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound(version.ToString())) : Result.Success(Info(found)));
        }

        public async Task<Result> DownloadAsync(ReleaseAsset asset, Stream destination, CancellationToken cancellationToken)
        {
            Downloads.Add(asset.Name);
            FakeRelease release = _releases.Single(candidate => ReleaseAssets.Tag(candidate.Version) == asset.Location);
            await destination.WriteAsync(release.Files[asset.Name], cancellationToken);
            return Result.Success();
        }

        private static ReleaseInfo Info(FakeRelease release) => new(
            release.Version,
            ReleaseAssets.Tag(release.Version),
            $"https://example.test/{ReleaseAssets.Tag(release.Version)}",
            [.. release.Files.Select(file => new ReleaseAsset(file.Key, file.Value.Length, ReleaseAssets.Tag(release.Version)))]);
    }

    private sealed class FakeInstaller(string rid, ReleaseVersion current) : IExecutableInstaller
    {
        public string? CannotReplaceBecause { get; set; }

        public Error? Replaceable { get; set; }

        /// <summary>What the "new binary" prints; by default whatever the archive says it is, and a passing self-check.</summary>
        public Dictionary<string, ProcessOutcome>? Outputs { get; set; }

        public FakeWorkspace? Workspace { get; private set; }

        public InstalledExecutable Locate() => new("/usr/local/bin/mysync", rid, current, CannotReplaceBecause);

        public Result CheckReplaceable(InstalledExecutable installed) => Replaceable is null ? Result.Success() : Result.Failure(Replaceable);

        public Task<Result<IUpdateWorkspace>> CreateWorkspaceAsync(InstalledExecutable installed, CancellationToken cancellationToken)
        {
            Workspace = new FakeWorkspace(this);
            return Task.FromResult(Result.Success<IUpdateWorkspace>(Workspace));
        }

        internal sealed class FakeWorkspace(FakeInstaller owner) : IUpdateWorkspace
        {
            private readonly Dictionary<string, KeptStream> _files = [];

            public List<string> Ran { get; } = [];

            public string? Installed { get; private set; }

            public bool Disposed { get; private set; }

            public Stream CreateFile(string name)
            {
                KeptStream file = new();
                _files[name] = file;
                return file;
            }

            // The fake archive is the binary itself: its bytes are "new mysync <version>".
            public Task<Result<string>> ExtractExecutableAsync(string archiveName, string executableName, CancellationToken cancellationToken) =>
                Task.FromResult(Result.Success(Encoding.UTF8.GetString(_files[archiveName].Kept)));

            public Task<Result<ProcessOutcome>> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            {
                string argument = arguments.Single();
                Ran.Add(argument);
                ProcessOutcome outcome = owner.Outputs?[argument] ?? (argument == "--version"
                    ? new ProcessOutcome(0, executable["new mysync ".Length..] + "\n")
                    : new ProcessOutcome(0, "ok"));
                return Task.FromResult(Result.Success(outcome));
            }

            public Task<Result> InstallAsync(string executable, InstalledExecutable target, CancellationToken cancellationToken)
            {
                Installed = executable;
                return Task.FromResult(Result.Success());
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }

        // A MemoryStream whose bytes stay readable after the handler disposes it.
        private sealed class KeptStream : MemoryStream
        {
            public byte[] Kept { get; private set; } = [];

            protected override void Dispose(bool disposing)
            {
                Kept = ToArray();
                base.Dispose(disposing);
            }
        }
    }
}
