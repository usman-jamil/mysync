using System.Text;
using MySync.Domain.Manifests;
using MySync.Domain.Publishing;
using MySync.Infrastructure.Publishing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Publishing;

public sealed class PublicationRegistrySerializerTests
{
    private readonly PublicationRegistrySerializer _serializer = new();

    private static Publication Sample(PublicationStatus status = PublicationStatus.Published) => new(
        PublishId.TryParse("4fQ9xK2mT7bV1nR8sL3pZa", out PublishId? id) ? id : throw new InvalidOperationException(),
        status, "Q3 reports", Revision.TryCreate(18, out Revision? revision) ? revision : throw new InvalidOperationException(),
        142, 40050611, new DateTimeOffset(2026, 11, 18, 7, 30, 0, TimeSpan.Zero), "usman@DEV-LAPTOP",
        "publishacct", "https://publishacct.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/", "sha256:592cc1ec0e8772de");

    [Fact]
    public void TheJson_Should_BePinned()
    {
        string json = Encoding.UTF8.GetString(_serializer.Serialize(PublicationRegistry.Empty.With(Sample())));

        json.ShouldBe("""
            {
              "formatVersion": 1,
              "publications": [
                {
                  "id": "4fQ9xK2mT7bV1nR8sL3pZa",
                  "status": "published",
                  "title": "Q3 reports",
                  "revision": 18,
                  "files": 142,
                  "bytes": 40050611,
                  "createdUtc": "2026-11-18T07:30:00Z",
                  "createdBy": "usman@DEV-LAPTOP",
                  "publishAccount": "publishacct",
                  "url": "https://publishacct.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/",
                  "siteAssetsVersion": "sha256:592cc1ec0e8772de"
                }
              ]
            }
            """.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(PublicationStatus.Pending)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    public void EveryStatus_Should_RoundTrip(PublicationStatus status)
    {
        PublicationRegistry registry = PublicationRegistry.Empty.With(Sample(status));

        Result<PublicationRegistry> read = _serializer.Deserialize(_serializer.Serialize(registry));

        read.IsSuccess.ShouldBeTrue();
        read.Value.Publications.Single().ShouldBe(registry.Publications.Single());
    }

    [Theory]
    [InlineData("""{ "formatVersion": 2, "publications": [] }""", "newer mysync")]
    [InlineData("""not json""", "valid JSON")]
    [InlineData("""{ "formatVersion": 1, "publications": [ { "id": "../../etc", "status": "published", "title": "t", "revision": 1, "files": 0, "bytes": 0, "createdUtc": "2026-01-01T00:00:00Z", "createdBy": "x", "publishAccount": "a", "url": "u", "siteAssetsVersion": "v" } ] }""", "malformed")]
    [InlineData("""{ "formatVersion": 1, "publications": [ { "id": "4fQ9xK2mT7bV1nR8sL3pZa", "status": "deleted", "title": "t", "revision": 1, "files": 0, "bytes": 0, "createdUtc": "2026-01-01T00:00:00Z", "createdBy": "x", "publishAccount": "a", "url": "u", "siteAssetsVersion": "v" } ] }""", "malformed")]
    public void AnUnreadableRegistry_Should_BeRefused(string json, string because)
    {
        Error error = _serializer.Deserialize(Encoding.UTF8.GetBytes(json)).Error;

        error.Code.ShouldBe("Publish.InvalidRegistry");
        error.Description.ShouldContain(because);
    }
}
