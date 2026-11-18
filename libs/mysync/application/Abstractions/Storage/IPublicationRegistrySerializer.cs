using MySync.Domain.Publishing;
using SharedKernel;

namespace MySync.Application.Abstractions.Storage;

/// <summary><c>publications.json</c> as bytes (Infrastructure, source-generated JSON), shared by every provider like the manifest's.</summary>
public interface IPublicationRegistrySerializer
{
    byte[] Serialize(PublicationRegistry registry);

    /// <summary><c>Publish.InvalidRegistry</c> for anything unreadable or of a newer format.</summary>
    Result<PublicationRegistry> Deserialize(ReadOnlySpan<byte> json);
}
