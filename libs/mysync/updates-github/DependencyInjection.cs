using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySync.Application.Abstractions.Updates;

namespace MySync.Updates.GitHub;

public static class DependencyInjection
{
    /// <summary>GitHub's REST API; the one host the token is sent to.</summary>
    public static Uri ApiBase { get; } = new("https://api.github.com/", UriKind.Absolute);

    /// <summary>
    /// Releases from a GitHub repository, the default source for <c>mysync update</c>.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c>.</param>
    /// <param name="userAgentVersion">This mysync's version: GitHub requires a User-Agent. Nothing about the user is sent.</param>
    /// <param name="token"><c>GITHUB_TOKEN</c>, for a private repository or a higher rate limit. Never logged.</param>
    public static IServiceCollection AddGitHubReleaseSource(this IServiceCollection services, string repository, string userAgentVersion, string? token = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        services.TryAddSingleton<IReleaseSource>(_ =>
        {
#pragma warning disable CA2000 // The HttpClient owns the handler and lives as long as the container.
            HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
#pragma warning restore CA2000
            {
                BaseAddress = ApiBase,
                Timeout = TimeSpan.FromMinutes(10), // a 35 MB archive on a slow line
            };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mysync", userAgentVersion));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return new GitHubReleaseSource(http, repository);
        });

        return services;
    }
}
