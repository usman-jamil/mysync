using System.Diagnostics;
using System.Text;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Publishing;

/// <summary>
/// The generated <c>download.sh</c>, run for real with <c>sh</c> and <c>curl</c> against a publication
/// laid out on disk (a <c>file://</c> base URL): hostile file names must land as files, never run.
/// </summary>
public sealed class DownloadScriptExecutionTests : IDisposable
{
    private static readonly string[] Names =
    [
        "$(touch pwned).txt", "`touch pwned`.txt", "a;touch pwned.txt", "it's 'quoted'.txt", "\"double\".txt",
        "-rf.txt", "‘curly’ quotes.txt", "$HOME.txt", "über/日本語 🎉.md", "sp ace/[x] #?%&+.bin", "deep/a/b/c.txt",
    ];

    private readonly TempShareFolder _published = new();
    private readonly TempShareFolder _downloads = new();

    [Fact]
    public async Task DownloadSh_Should_FetchAndVerifyEveryFile_WithoutRunningAnyName()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "download.sh is for macOS and Linux.");

        SiteData data = Publish();

        (int exit, string output) = await RunAsync();

        exit.ShouldBe(0, output);
        foreach (string name in Names)
        {
            _downloads.ReadText(name).ShouldBe("content of " + name, name);
        }

        _downloads.Exists("empty.txt").ShouldBeTrue();
        new FileInfo(_downloads.Combine("empty.txt")).Length.ShouldBe(0);
        Directory.Exists(_downloads.Combine("logs")).ShouldBeTrue();
        _downloads.Exists("pwned").ShouldBeFalse("a file name must never run as a command");
        _published.Exists("pwned").ShouldBeFalse();
        output.ShouldContain($"Downloaded and verified {data.TotalFiles} files.");
    }

    [Fact]
    public async Task DownloadSh_Should_StopAtAFileThatDoesNotMatchItsHash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "download.sh is for macOS and Linux.");

        Publish();
        await File.WriteAllTextAsync(_published.Combine("files/" + "deep/a/b/c.txt"), "tampered", TestContext.Current.CancellationToken);

        (int exit, string output) = await RunAsync();

        exit.ShouldNotBe(0);
        output.ShouldContain("deep/a/b/c.txt does not match its SHA-256; stopping.");
    }

    private SiteData Publish()
    {
        ManifestBuilder builder = new();
        foreach (string name in Names)
        {
            builder.WithFile(name, "content of " + name);
            _published.WriteFile("files/" + name, "content of " + name);
        }

        Manifest manifest = builder.WithFile("empty.txt", string.Empty).WithDirectory("logs").Build();
        SiteData data = SiteDataBuilder.Build(manifest, new SiteDataPublication(PublishId.New(), "t", DateTimeOffset.UnixEpoch, manifest.Revision, "test")).Data;
        _published.WriteFile(PublicationLayout.ShellScript, Encoding.UTF8.GetBytes(DownloadScripts.Shell(data, new Uri(_published.Path + "/"))));
        return data;
    }

    private async Task<(int Exit, string Output)> RunAsync()
    {
        ProcessStartInfo start = new("sh", [_published.Combine(PublicationLayout.ShellScript)])
        {
            WorkingDirectory = _downloads.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose()
    {
        _published.Dispose();
        _downloads.Dispose();
    }
}
