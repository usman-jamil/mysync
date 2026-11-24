using System.Net;
using MySync.Application.Abstractions.Publishing;

namespace MySync.Infrastructure.Publishing;

/// <summary>
/// Fetches a publication's <c>site-data.js</c> the way anyone with the link would: a plain GET,
/// no credentials, no API keys. A few tries, since a fresh blob can take a moment to be served.
/// </summary>
internal sealed class HttpPublicationVerifier(HttpClient http, TimeProvider timeProvider) : IPublicationVerifier
{
    private const int Attempts = 3;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    public async Task<VerificationOutcome> VerifyAsync(Uri siteDataUrl, string publishId, IReadOnlySet<int> notServedStatusCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(siteDataUrl);
        ArgumentNullException.ThrowIfNull(notServedStatusCodes);
        string detail = "no answer";

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(siteDataUrl, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (body.Contains($"\"{publishId}\"", StringComparison.Ordinal))
                    {
                        return new VerificationOutcome(true);
                    }

                    detail = "the page answered, but with another publication's data";
                }
                else if (notServedStatusCodes.Contains((int)response.StatusCode))
                {
                    // S3 answers 403 for a missing key to anyone who can't list the bucket.
                    detail = $"not served: HTTP {(int)response.StatusCode} {response.ReasonPhrase} (missing, or not public)";
                }
                else
                {
                    detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch (HttpRequestException exception)
            {
                detail = exception.HttpRequestError.ToString();
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                detail = "timed out";
            }

            if (attempt < Attempts)
            {
                await Task.Delay(Pause, timeProvider, cancellationToken);
            }
        }

        return new VerificationOutcome(false, detail);
    }
}
