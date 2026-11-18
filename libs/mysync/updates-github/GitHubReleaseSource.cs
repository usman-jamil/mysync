using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Updates.GitHub;

/// <summary>
/// Releases from GitHub (<c>https://api.github.com/repos/&lt;owner&gt;/&lt;repo&gt;/releases</c>). Only
/// tags starting with <c>mysync-</c> count; drafts never do. Assets are fetched through the API's
/// asset URL, which works for public repositories without a token and for private ones with
/// <c>GITHUB_TOKEN</c>.
/// </summary>
/// <remarks>
/// The token (if any) is sent only to api.github.com: .NET drops the Authorization header when a
/// download redirects to GitHub's file host. It never appears in an error or a log line.
/// </remarks>
internal sealed class GitHubReleaseSource(HttpClient http, string repository) : IReleaseSource
{
    /// <summary>The most releases one listing returns; mysync won't have that many for a long time.</summary>
    private const int PageSize = 100;

    public string Description => $"GitHub {repository}";

    public async Task<Result<ReleaseInfo>> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        Result<List<GitHubRelease>> listed = await GetJsonAsync(
            $"repos/{repository}/releases?per_page={PageSize}", GitHubJsonContext.Default.ListGitHubRelease, $"in {repository}", cancellationToken);
        if (listed.IsFailure)
        {
            return Result.Failure<ReleaseInfo>(listed.Error);
        }

        ReleaseInfo? latest = listed.Value
            .Where(release => !release.Draft)
            .Select(ToRelease)
            .OfType<ReleaseInfo>()
            .Where(release => includePrereleases || !release.Version.IsPrerelease)
            .OrderDescending(Comparer<ReleaseInfo>.Create((left, right) => left.Version.CompareTo(right.Version)))
            .FirstOrDefault();

        return latest is null
            ? Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound($"in {repository}"))
            : Result.Success(latest);
    }

    public async Task<Result<ReleaseInfo>> GetAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        Result<GitHubRelease> found = await GetJsonAsync(
            $"repos/{repository}/releases/tags/{Uri.EscapeDataString(ReleaseAssets.Tag(version))}", GitHubJsonContext.Default.GitHubRelease, $"{version} in {repository}", cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<ReleaseInfo>(found.Error);
        }

        return ToRelease(found.Value) is { } release && !found.Value.Draft
            ? Result.Success(release)
            : Result.Failure<ReleaseInfo>(UpdateErrors.ReleaseNotFound($"{version} in {repository}"));
    }

    public async Task<Result> DownloadAsync(ReleaseAsset asset, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(destination);

        using HttpRequestMessage request = new(HttpMethod.Get, asset.Location);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure(Failure(response, asset.Name));
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await body.CopyToAsync(destination, cancellationToken);
            return Result.Success();
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return Result.Failure(UpdateErrors.SourceUnreachable($"downloading {asset.Name} from GitHub failed ({exception.GetType().Name})"));
        }
    }

    private static ReleaseInfo? ToRelease(GitHubRelease release)
    {
        if (!release.TagName.StartsWith(ReleaseAssets.TagPrefix, StringComparison.Ordinal) || !ReleaseVersion.TryParse(release.TagName, out ReleaseVersion? version))
        {
            return null;
        }

        return new ReleaseInfo(version, release.TagName, release.HtmlUrl, [.. release.Assets.Select(asset => new ReleaseAsset(asset.Name, asset.Size, asset.Url))]);
    }

    private async Task<Result<T>> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string what, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<T>(UpdateErrors.ReleaseNotFound(what, "If the repository is private, set GITHUB_TOKEN."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<T>(Failure(response, what));
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            T? value = await JsonSerializer.DeserializeAsync(body, type, cancellationToken);
            return value is null
                ? Result.Failure<T>(UpdateErrors.SourceUnreachable("GitHub sent an empty answer"))
                : Result.Success(value);
        }
        catch (JsonException)
        {
            return Result.Failure<T>(UpdateErrors.SourceUnreachable("GitHub's answer could not be read"));
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return Result.Failure<T>(UpdateErrors.SourceUnreachable($"GitHub could not be reached ({exception.GetType().Name})"));
        }
    }

    private static Error Failure(HttpResponseMessage response, string what)
    {
        bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
                && remaining.FirstOrDefault() == "0";

        if (rateLimited)
        {
            return UpdateErrors.SourceUnreachable("GitHub's rate limit was reached. Wait an hour, or set GITHUB_TOKEN.");
        }

        return response.StatusCode == HttpStatusCode.Unauthorized
            ? UpdateErrors.SourceUnreachable("GitHub rejected GITHUB_TOKEN.")
            : UpdateErrors.SourceUnreachable($"GitHub answered {(int)response.StatusCode} for {what}.");
    }

    private static bool IsTransport(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("url")] string Url);

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
