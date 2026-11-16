using MySync.Domain.Manifests;
using SharedKernel;

namespace MySync.Application.Abstractions.Storage;

/// <summary>
/// The one manifest JSON format (CLAUDE.md §6), shared by every provider so they write
/// byte-identical manifests. Implemented in Infrastructure with a source-generated context.
/// </summary>
public interface IManifestSerializer
{
    byte[] Serialize(Manifest manifest);

    /// <summary>
    /// Parses and validates through the Domain (<see cref="Manifest.Restore"/>, <c>SharePath</c>):
    /// <c>Shares.UnsupportedFormat</c> for a newer format, <c>Shares.InvalidManifest</c> for anything malformed.
    /// </summary>
    Result<Manifest> Deserialize(ReadOnlySpan<byte> json);
}
