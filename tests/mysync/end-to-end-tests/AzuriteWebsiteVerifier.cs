using Azure;
using Azure.Storage.Blobs;
using MySync.Application.Abstractions.Publishing;

namespace MySync.EndToEndTests;

/// <summary>
/// Stands in for the static website endpoint Azurite doesn't have: the URL's path is the
/// <c>$web</c> blob the endpoint would serve. Everything else about verification (checking the
/// publication ID in <c>site-data.js</c>) is the same.
/// </summary>
internal sealed class AzuriteWebsiteVerifier(string publishConnectionString) : IPublicationVerifier
{
    public async Task<VerificationOutcome> VerifyAsync(Uri siteDataUrl, string publishId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(siteDataUrl);
        BlobClient blob = new BlobServiceClient(publishConnectionString).GetBlobContainerClient("$web").GetBlobClient(Uri.UnescapeDataString(siteDataUrl.AbsolutePath.TrimStart('/')));
        try
        {
            string body = (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToString();
            return body.Contains($"\"{publishId}\"", StringComparison.Ordinal) ? new VerificationOutcome(true) : new VerificationOutcome(false, "another publication's data");
        }
        catch (RequestFailedException exception)
        {
            return new VerificationOutcome(false, $"HTTP {exception.Status}");
        }
    }
}
