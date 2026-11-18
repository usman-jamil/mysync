using System.Net;
using System.Text;
using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using MySync.Infrastructure.Updates;
using MySync.Testing;
using MySync.Updates.GitHub;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Updates;

/// <summary>Where releases come from: a folder laid out like GitHub releases, and GitHub itself (against a fake HTTP handler).</summary>
public sealed class ReleaseSourceTests : IDisposable
{
    private readonly TempShareFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFolder_Should_OfferTheNewestRelease_LeavingOutPrereleasesAndOtherFolders()
    {
        _temp.WriteFile("mysync-0.3.0/SHA256SUMS.txt", "a").WriteFile("mysync-0.10.0/SHA256SUMS.txt", "b")
            .WriteFile("mysync-1.0.0-beta.1/SHA256SUMS.txt", "c").WriteFile("notes/readme.md", "not a release")
            .WriteFile("9.9.9/SHA256SUMS.txt", "no tag prefix");
        DirectoryReleaseSource source = new(_temp.Path);

        Ok(await source.GetLatestAsync(includePrereleases: false, Ct)).Version.ToString().ShouldBe("0.10.0");
        Ok(await source.GetLatestAsync(includePrereleases: true, Ct)).Version.ToString().ShouldBe("1.0.0-beta.1");
        ReleaseInfo pinned = Ok(await source.GetAsync(V("0.3.0"), Ct));
        pinned.Find("SHA256SUMS.txt")!.Size.ShouldBe(1);
        (await source.GetAsync(V("5.0.0"), Ct)).Error.Code.ShouldBe("Update.ReleaseNotFound");
        (await new DirectoryReleaseSource(_temp.Combine("missing")).GetLatestAsync(false, Ct)).Error.Code.ShouldBe("Update.ReleaseNotFound");
    }

    [Fact]
    public async Task AFolder_Should_DownloadAnAssetsBytes()
    {
        _temp.WriteFile("mysync-1.0.0/SHA256SUMS.txt", "checksums");
        DirectoryReleaseSource source = new(_temp.Path);
        ReleaseInfo release = Ok(await source.GetLatestAsync(false, Ct));
        using MemoryStream into = new();

        (await source.DownloadAsync(release.Find("SHA256SUMS.txt")!, into, Ct)).IsSuccess.ShouldBeTrue();

        Encoding.UTF8.GetString(into.ToArray()).ShouldBe("checksums");
    }

    [Fact]
    public async Task GitHub_Should_PickTheNewestMysyncRelease_IgnoringDraftsAndOtherTags()
    {
        using FakeGitHub github = new();
        github.Respond("repos/acme/mysync/releases?per_page=100", HttpStatusCode.OK, """
            [
              { "tag_name": "mysync-0.3.0", "draft": false, "prerelease": false, "html_url": "https://github.com/acme/mysync/releases/tag/mysync-0.3.0",
                "assets": [ { "name": "SHA256SUMS.txt", "size": 669, "url": "https://api.github.com/repos/acme/mysync/releases/assets/1" } ] },
              { "tag_name": "mysync-0.4.0", "draft": true, "prerelease": false, "html_url": null, "assets": [] },
              { "tag_name": "mysync-0.5.0-beta.1", "draft": false, "prerelease": true, "html_url": null, "assets": [] },
              { "tag_name": "other-tool-9.0.0", "draft": false, "prerelease": false, "html_url": null, "assets": [] }
            ]
            """);
        GitHubReleaseSource source = github.Source("acme/mysync");

        ReleaseInfo latest = Ok(await source.GetLatestAsync(includePrereleases: false, Ct));
        latest.Version.ToString().ShouldBe("0.3.0");
        latest.NotesUrl.ShouldBe("https://github.com/acme/mysync/releases/tag/mysync-0.3.0");
        latest.Find("SHA256SUMS.txt")!.Location.ShouldBe("https://api.github.com/repos/acme/mysync/releases/assets/1");
        Ok(await source.GetLatestAsync(includePrereleases: true, Ct)).Version.ToString().ShouldBe("0.5.0-beta.1");
        source.Description.ShouldBe("GitHub acme/mysync");
    }

    [Fact]
    public async Task GitHub_Should_FetchATag_AndSayWhenItDoesntExist()
    {
        using FakeGitHub github = new();
        github.Respond("repos/acme/mysync/releases/tags/mysync-0.3.0", HttpStatusCode.OK,
            """{ "tag_name": "mysync-0.3.0", "draft": false, "prerelease": false, "html_url": null, "assets": [] }""");
        github.Respond("repos/acme/mysync/releases/tags/mysync-7.0.0", HttpStatusCode.NotFound, """{ "message": "Not Found" }""");
        GitHubReleaseSource source = github.Source("acme/mysync");

        Ok(await source.GetAsync(V("0.3.0"), Ct)).Tag.ShouldBe("mysync-0.3.0");
        Error missing = (await source.GetAsync(V("7.0.0"), Ct)).Error;
        missing.Code.ShouldBe("Update.ReleaseNotFound");
        missing.Description.ShouldContain("GITHUB_TOKEN");
    }

    [Fact]
    public async Task GitHub_Should_ExplainARateLimit_AndNeverEchoTheToken()
    {
        using FakeGitHub github = new();
        github.Respond("repos/acme/mysync/releases?per_page=100", HttpStatusCode.Forbidden, "{}", ("X-RateLimit-Remaining", "0"));
        GitHubReleaseSource source = github.Source("acme/mysync", token: "ghp_secretToken123");

        Error error = (await source.GetLatestAsync(false, Ct)).Error;

        error.Code.ShouldBe("Update.SourceUnreachable");
        error.Description.ShouldContain("set GITHUB_TOKEN");
        error.Description.ShouldNotContain("ghp_secretToken123");
        github.Requests.Single().Headers.Authorization!.ToString().ShouldBe("Bearer ghp_secretToken123");
        github.Requests.Single().Headers.UserAgent.ToString().ShouldBe("mysync/1.2.3");
    }

    [Fact]
    public async Task GitHub_Should_DownloadAssetsAsOctetStreams()
    {
        using FakeGitHub github = new();
        github.Respond("repos/acme/mysync/releases/assets/7", HttpStatusCode.OK, "archive bytes");
        GitHubReleaseSource source = github.Source("acme/mysync");
        using MemoryStream into = new();

        (await source.DownloadAsync(new ReleaseAsset("a.tar.gz", 13, "https://api.github.com/repos/acme/mysync/releases/assets/7"), into, Ct)).IsSuccess.ShouldBeTrue();

        Encoding.UTF8.GetString(into.ToArray()).ShouldBe("archive bytes");
        github.Requests.Single().Headers.Accept.ToString().ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task GitHub_Should_ReportANetworkFailure_AsUnreachable()
    {
        using FakeGitHub github = new() { Throw = new HttpRequestException("no route to host") };

        (await github.Source("acme/mysync").GetLatestAsync(false, Ct)).Error.Code.ShouldBe("Update.SourceUnreachable");
    }

    public void Dispose() => _temp.Dispose();

    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out ReleaseVersion? version) ? version : throw new ArgumentException(text);

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    /// <summary>Answers requests from a table, and keeps them to inspect.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, (string Name, string Value)[] Headers)> _answers = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public Exception? Throw { get; init; }

        public void Respond(string pathAndQuery, HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
            _answers["/" + pathAndQuery] = (status, body, headers);

        public GitHubReleaseSource Source(string repository, string? token = null)
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
            services.AddGitHubReleaseSource(repository, "1.2.3", token);
            IReleaseSource built = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IReleaseSource>(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));

            // Same headers and base address as production, but answered here.
#pragma warning disable CA2000 // The client lives as long as the source under test.
            HttpClient http = new(this) { BaseAddress = MySync.Updates.GitHub.DependencyInjection.ApiBase };
#pragma warning restore CA2000
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mysync/1.2.3");
            if (token is not null)
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            built.Description.ShouldBe($"GitHub {repository}");
            return new GitHubReleaseSource(http, repository);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw is not null)
            {
                throw Throw;
            }

            (HttpStatusCode status, string body, (string Name, string Value)[] headers) =
                _answers.TryGetValue(request.RequestUri!.PathAndQuery, out (HttpStatusCode Status, string Body, (string Name, string Value)[] Headers) answer)
                    ? answer
                    : (HttpStatusCode.NotFound, "{}", []);
            HttpResponseMessage response = new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
            foreach ((string name, string value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }
}
