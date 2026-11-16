using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Storage;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Storage;

/// <summary>
/// Blob names are a storage format: every client ever released must compute the same ones, so
/// these tests pin exact strings rather than re-deriving them.
/// </summary>
public class ShareLayoutTests
{
    private static readonly ShareLayout Layout = ShareLayout.For(Share());

    [Fact]
    public void Names_Should_MatchTheDocumentedLayout()
    {
        Layout.Prefix.ShouldBe("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/");
        Layout.Manifest.ShouldBe("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifest.json");
        Layout.ManifestHistory(Rev(17)).ShouldBe("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/0000000017.json");
        Layout.ObjectBlob(Hash(HashOfTest)).ShouldBe(
            "shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/9f/9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
    }

    [Fact]
    public void ManifestHistoryNames_Should_SortInRevisionOrder()
    {
        string[] names = [Layout.ManifestHistory(Rev(10)), Layout.ManifestHistory(Rev(9)), Layout.ManifestHistory(Rev(100))];

        names.Order(StringComparer.Ordinal).ShouldBe([
            Layout.ManifestHistory(Rev(9)), Layout.ManifestHistory(Rev(10)), Layout.ManifestHistory(Rev(100))]);
    }

    [Fact]
    public void TryParseObject_Should_RoundTrip()
    {
        Layout.TryParseObject(Layout.ObjectBlob(Hash(HashOfTest)), out ContentHash? hash).ShouldBeTrue();

        hash.ShouldBe(Hash(HashOfTest));
    }

    [Theory]
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/aa/9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")] // wrong fan-out
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/9F/9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")] // uppercase
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")] // no fan-out
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/9f/9f86d0")] // truncated
    [InlineData("shares/11111111-8f0a-4c43-9d3e-5a1f2b7c9e10/objects/9f/9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")] // other share
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifest.json")]
    public void TryParseObject_Should_RejectAnythingButACanonicalObjectName(string blobName)
    {
        Layout.TryParseObject(blobName, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseManifestHistory_Should_RoundTrip()
    {
        Layout.TryParseManifestHistory(Layout.ManifestHistory(Rev(17)), out Revision? revision).ShouldBeTrue();

        revision.ShouldBe(Rev(17));
    }

    [Theory]
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/17.json")] // not padded
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/0000000000.json")] // revision 0
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/+000000017.json")]
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/0000000017.json.tmp")]
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifests/.json")]
    [InlineData("shares/0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10/manifest.json")]
    public void TryParseManifestHistory_Should_RejectNonCanonicalNames(string blobName)
    {
        Layout.TryParseManifestHistory(blobName, out _).ShouldBeFalse();
    }
}
