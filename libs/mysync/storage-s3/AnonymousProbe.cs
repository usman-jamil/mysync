using System.Net;

namespace MySync.Storage.S3;

/// <summary>
/// Reads the publishing bucket the way anyone with a link would: a plain GET through
/// <c>MYSYNC_PUBLISH_BASE_URL</c>, no credentials, no signature. The S3 publish target's preflight
/// uses it to prove a publication would be readable, and that the bucket can't be listed.
/// </summary>
internal sealed class AnonymousProbe(HttpClient http)
{
    private const char Slash = '/';

    private const int Attempts = 3;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>Whether <paramref name="url"/> answers 200 with exactly <paramref name="expected"/> (a few tries: CloudFront can take a moment).</summary>
    public async Task<bool> ServesAsync(Uri url, string expected, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await GetAsync(url, cancellationToken) is (HttpStatusCode.OK, { } body) && string.Equals(body.Trim(), expected, StringComparison.Ordinal))
            {
                return true;
            }

            if (attempt < Attempts)
            {
                await Task.Delay(Pause, timeProvider, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an anonymous ListObjectsV2 on the publishing prefix succeeds (<c>GET &lt;base&gt;/?list-type=2&amp;prefix=p/</c>).
    /// Anything but a 200 (403, a CloudFront error page, no answer) means it can't be listed.
    /// </summary>
    public async Task<bool> CanListAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        Uri root = baseUrl.AbsoluteUri.EndsWith(Slash) ? baseUrl : new Uri(baseUrl.AbsoluteUri + Slash);
        return await GetAsync(new Uri(root, "?list-type=2&prefix=p/"), cancellationToken) is (HttpStatusCode.OK, { } body)
            && body.Contains("<ListBucketResult", StringComparison.Ordinal);
    }

    private async Task<(HttpStatusCode Status, string? Body)?> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string? body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
            return (response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timed out
        }
    }
}
