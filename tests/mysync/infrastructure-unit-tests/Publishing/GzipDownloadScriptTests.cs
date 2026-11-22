using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Publishing;

/// <summary>
/// A compressed share's published files are served with <c>Content-Encoding: gzip</c> (CLAUDE.md
/// §10a). Both download scripts, run for real against a local HTTP server, must end up with the
/// original bytes and verify the SHA-256 of the decoded file. <c>download.ps1</c> also decodes by
/// itself when PowerShell didn't (served here without the header), and never touches a real
/// <c>.gz</c> file.
/// </summary>
public sealed class GzipDownloadScriptTests : IDisposable
{
    private static readonly string Log = string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i} of a log that compresses well\n"));
    private static readonly byte[] RealGz = Gzip(Encoding.UTF8.GetBytes("an archive someone shared on purpose\n"));

    private readonly TempShareFolder _downloads = new();
    private readonly HttpListener _server = new();
    private readonly Dictionary<string, (byte[] Body, string? Encoding)> _files = new(StringComparer.Ordinal);
    private readonly Uri _base;

    public GzipDownloadScriptTests()
    {
        int port = FreePort();
        _base = new Uri($"http://127.0.0.1:{port}/p/test/");
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = ServeAsync();
    }

    [Fact]
    public async Task DownloadSh_Should_DecodeGzipResponses_AndVerifyTheDecodedFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "download.sh is for macOS and Linux.");
        SiteData data = Publish(declareEncoding: true);

        (int exit, string output) = await RunAsync("sh", "download.sh", DownloadScripts.Shell(data, _base));

        exit.ShouldBe(0, output);
        _downloads.ReadText("logs/app.log").ShouldBe(Log);
        (await File.ReadAllBytesAsync(_downloads.Combine("archive.gz"), TestContext.Current.CancellationToken)).ShouldBe(RealGz);
    }

    [Theory]
    [InlineData(true)]  // the header is there: PowerShell may decode it itself
    [InlineData(false)] // gzip bytes without the header, as an older PowerShell would leave them: the script decodes
    public async Task DownloadPs1_Should_EndUpWithTheOriginalBytes(bool declareEncoding)
    {
        Assert.SkipUnless(PowerShellAvailable.Value, "pwsh is not installed");
        SiteData data = Publish(declareEncoding);

        (int exit, string output) = await RunAsync("pwsh", "download.ps1", DownloadScripts.PowerShell(data, _base), "-NoProfile", "-NonInteractive", "-File");

        exit.ShouldBe(0, output);
        _downloads.ReadText("logs/app.log").ShouldBe(Log);
        (await File.ReadAllBytesAsync(_downloads.Combine("archive.gz"), TestContext.Current.CancellationToken)).ShouldBe(RealGz);
    }

    private static readonly Lazy<bool> PowerShellAvailable = new(() =>
    {
        try
        {
            using Process pwsh = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-Command", "exit 0"]) { RedirectStandardOutput = true })!;
            pwsh.WaitForExit();
            return pwsh.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private SiteData Publish(bool declareEncoding)
    {
        Manifest manifest = new ManifestBuilder().WithFile("logs/app.log", Log).WithFile("archive.gz", RealGz).Build();
        SiteData data = SiteDataBuilder.Build(manifest, new SiteDataPublication(PublishId.New(), "t", DateTimeOffset.UnixEpoch, manifest.Revision, "test")).Data;
        _files["/p/test/files/logs/app.log"] = (Gzip(Encoding.UTF8.GetBytes(Log)), declareEncoding ? "gzip" : null);
        _files["/p/test/files/archive.gz"] = (RealGz, null); // stored raw: it's already compressed
        return data;
    }

    private async Task<(int Exit, string Output)> RunAsync(string shell, string name, string script, params string[] options)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mysync-{Guid.NewGuid():N}-{name}");
        await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);
        try
        {
            ProcessStartInfo start = new(shell, [.. options, path])
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
        finally
        {
            File.Delete(path);
        }
    }

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _server.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            string path = context.Request.Url!.AbsolutePath;
            if (_files.TryGetValue(path, out (byte[] Body, string? Encoding) file))
            {
                if (file.Encoding is { } encoding)
                {
                    context.Response.AddHeader("Content-Encoding", encoding);
                }

                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = file.Body.Length;
                await context.Response.OutputStream.WriteAsync(file.Body);
            }
            else
            {
                context.Response.StatusCode = 404;
            }

            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        _server.Close();
        _downloads.Dispose();
    }
}
