using Shouldly;
using Xunit;

namespace MySync.Storage.Azure.IntegrationTests;

public class ConnectionStringRedactorTests
{
    [Theory]
    [InlineData(
        "DefaultEndpointsProtocol=https;AccountName=team;AccountKey=c2VjcmV0+/==;EndpointSuffix=core.windows.net",
        "DefaultEndpointsProtocol=https;AccountName=team;AccountKey=***;EndpointSuffix=core.windows.net")]
    [InlineData(
        "BlobEndpoint=https://team.blob.core.windows.net/;SharedAccessSignature=sv=2024-08-04&ss=b&sig=abc%2Bdef%3D",
        "BlobEndpoint=https://team.blob.core.windows.net/;SharedAccessSignature=***")]
    [InlineData(
        "GET https://team.blob.core.windows.net/mysync/x?sv=2024-08-04&se=2026&sig=abc%2Bdef failed",
        "GET https://team.blob.core.windows.net/mysync/x?sv=2024-08-04&se=2026&sig=*** failed")]
    [InlineData("accountkey = abc123 then text", "accountkey=*** then text")]
    [InlineData("nothing secret here", "nothing secret here")]
    [InlineData("", "")]
    public void Redact_Should_MaskKeysAndSignatures_AndLeaveTheRest(string text, string expected)
    {
        ConnectionStringRedactor.Redact(text).ShouldBe(expected);
    }
}
