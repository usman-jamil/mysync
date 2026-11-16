using MySync.Domain.Manifests;
using Shouldly;

namespace MySync.Testing;

public static class ManifestAssertions
{
    /// <summary>Same share, revision, timestamps, author and entries (Manifest has no value equality).</summary>
    public static void ShouldMatchManifest(this Manifest actual, Manifest expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);

        actual.ShareId.ShouldBe(expected.ShareId);
        actual.Revision.ShouldBe(expected.Revision);
        actual.CreatedUtc.ShouldBe(expected.CreatedUtc);
        actual.UpdatedUtc.ShouldBe(expected.UpdatedUtc);
        actual.UpdatedBy.ShouldBe(expected.UpdatedBy);
        actual.Entries.Select(pair => (pair.Key.Value, pair.Value))
            .ShouldBe(expected.Entries.Select(pair => (pair.Key.Value, pair.Value)));
    }
}
