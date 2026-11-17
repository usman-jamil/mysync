using System.Text;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Infrastructure.Serialization;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Serialization;

/// <summary>The manifest JSON format (CLAUDE.md §6). Changing <see cref="Serialize_Should_WriteExactlyThisJson"/> changes the wire format.</summary>
public class ManifestSerializerTests
{
    private static readonly ShareId Id = Parse("0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10");
    private static readonly ContentHash Readme = ManifestBuilder.HashOf("# hi");
    private static readonly ContentHash Deploy = ManifestBuilder.HashOf("#!/bin/sh");

    private readonly ManifestSerializer _serializer = new();

    [Fact]
    public void Serialize_Should_WriteExactlyThisJson()
    {
        string json = Encoding.UTF8.GetString(_serializer.Serialize(Sample()));

        json.ShouldBe($$"""
            {
              "formatVersion": 1,
              "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10",
              "revision": 17,
              "createdUtc": "2026-11-15T18:40:12Z",
              "updatedUtc": "2026-11-16T09:02:55Z",
              "updatedBy": "usman@DEV-LAPTOP",
              "entries": {
                "docs/readme.md": {
                  "type": "file",
                  "sha256": "{{Readme}}",
                  "size": 4,
                  "mtimeUtc": "2026-11-15T18:31:02.1234567Z",
                  "exec": false
                },
                "logs": {
                  "type": "dir"
                },
                "scripts/deploy.sh": {
                  "type": "file",
                  "sha256": "{{Deploy}}",
                  "size": 9,
                  "mtimeUtc": "2026-11-14T10:12:44Z",
                  "exec": true
                }
              }
            }
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Serialize_Should_BeDeterministic_UTF8WithoutABom_AndLfOnly()
    {
        byte[] first = _serializer.Serialize(Sample());
        byte[] second = _serializer.Serialize(Sample(reversed: true));

        second.ShouldBe(first);
        first[0].ShouldBe((byte)'{');
        first.ShouldNotContain((byte)'\r');
    }

    [Fact]
    public void RoundTrip_Should_KeepEveryFieldExactly_IncludingSubSecondMtimes()
    {
        Manifest original = Sample();

        Manifest read = _serializer.Deserialize(_serializer.Serialize(original)).Value;

        read.ShareId.ShouldBe(original.ShareId);
        read.Revision.ShouldBe(original.Revision);
        read.CreatedUtc.ShouldBe(original.CreatedUtc);
        read.UpdatedUtc.ShouldBe(original.UpdatedUtc);
        read.UpdatedBy.ShouldBe(original.UpdatedBy);
        read.Entries.ShouldBe(original.Entries);
        read.Entries[P("docs/readme.md")].ShouldBeOfType<FileEntry>().MtimeUtc.Ticks.ShouldBe(original.Entries[P("docs/readme.md")].ShouldBeOfType<FileEntry>().MtimeUtc.Ticks);
    }

    [Fact]
    public void NonAsciiPaths_Should_RoundTrip()
    {
        Manifest original = Manifest.CreateNew(Id, [new(P("résumé/日本語 ✓.txt"), File(Readme, 4, false))], DateTimeOffset.UnixEpoch, "a@b").Value;

        _serializer.Deserialize(_serializer.Serialize(original)).Value.Entries.ShouldBe(original.Entries);
    }

    [Fact]
    public void ANewerFormat_Should_BeUnsupported_EvenIfTheRestOfItIsUnreadable()
    {
        Error error = Read("""{ "formatVersion": 2, "somethingNew": [1, 2, 3] }""").Error;

        error.ShouldBe(ShareErrors.UnsupportedFormat(2));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "formatVersion": 1 }""")]
    public void MalformedJson_Should_BeAnInvalidManifest(string json)
    {
        Read(json).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Theory]
    [InlineData("\"../escape.txt\"")]
    [InlineData("\"/etc/passwd\"")]
    [InlineData("\"C:/Windows/x\"")]
    [InlineData("\"a\\\\..\\\\..\\\\x\"")]
    [InlineData("\"bad\\u0007bell\"")]
    public void ACraftedPath_Should_BeRejected_BeforeAnythingTouchesDisk(string key)
    {
        Read(WithEntries($$"""{{key}}: { "type": "dir" }""")).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Theory]
    [InlineData("""{ "type": "file", "sha256": "xyz", "size": 1, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false }""")]
    [InlineData("""{ "type": "file", "size": 1, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false }""")]
    [InlineData("""{ "type": "file", "sha256": "SHA", "size": -1, "mtimeUtc": "2026-11-15T18:31:02Z", "exec": false }""")]
    [InlineData("""{ "type": "symlink" }""")]
    [InlineData("""{ "type": "file", "sha256": "SHA", "size": 1, "mtimeUtc": "yesterday", "exec": false }""")]
    public void AnInvalidEntry_Should_BeRejected(string entry)
    {
        Read(WithEntries($"\"a.txt\": {entry.Replace("SHA", Readme.Value, StringComparison.Ordinal)}")).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void TheSamePathTwice_Should_BeRejected_LiterallyOrAsNfcAndNfd()
    {
        Read(WithEntries("""
            "a": { "type": "dir" },
            "a": { "type": "dir" }
            """)).Error.Code.ShouldBe("Shares.InvalidManifest");
        Read(WithEntries("""
            "caf\u00e9": { "type": "dir" },
            "cafe\u0301": { "type": "dir" }
            """)).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void ARepeatedTopLevelProperty_Should_BeRejected()
    {
        string json = Encoding.UTF8.GetString(_serializer.Serialize(Sample())).Replace("\"revision\": 17,", "\"revision\": 17, \"revision\": 99,", StringComparison.Ordinal);

        Read(json).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    [Fact]
    public void UnknownProperties_Should_BeSkipped()
    {
        string json = Encoding.UTF8.GetString(_serializer.Serialize(Sample())).Replace("\"revision\": 17,", "\"revision\": 17, \"addedLater\": true,", StringComparison.Ordinal);

        Read(json).Value.Revision.Value.ShouldBe(17);
    }

    [Fact]
    public void InvalidHeaderValues_Should_BeRejected()
    {
        string good = Encoding.UTF8.GetString(_serializer.Serialize(Sample()));

        Read(good.Replace(Id.ToString(), "not-a-uuid", StringComparison.Ordinal)).Error.Code.ShouldBe("Shares.InvalidManifest");
        Read(good.Replace("\"revision\": 17", "\"revision\": 0", StringComparison.Ordinal)).Error.Code.ShouldBe("Shares.InvalidManifest");
    }

    private Result<Manifest> Read(string json) => _serializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static string WithEntries(string entries) => $$"""
        {
          "formatVersion": 1, "shareId": "{{Id}}", "revision": 1,
          "createdUtc": "2026-11-15T18:40:12Z", "updatedUtc": "2026-11-15T18:40:12Z", "updatedBy": "x",
          "entries": { {{entries}} }
        }
        """;

    private static Manifest Sample(bool reversed = false)
    {
        List<KeyValuePair<SharePath, ManifestEntry>> entries =
        [
            new(P("docs/readme.md"), File(Readme, 4, false, new DateTimeOffset(2026, 11, 15, 18, 31, 2, TimeSpan.Zero).AddTicks(1_234_567))),
            new(P("scripts/deploy.sh"), File(Deploy, 9, true, new DateTimeOffset(2026, 11, 14, 10, 12, 44, TimeSpan.Zero))),
            new(P("logs"), DirectoryEntry.Instance),
        ];
        if (reversed)
        {
            entries.Reverse();
        }

        return Manifest.Restore(
            1,
            Id,
            Revision(17),
            new DateTimeOffset(2026, 11, 15, 18, 40, 12, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 16, 11, 2, 55, TimeSpan.FromHours(2)), // not UTC on purpose
            "usman@DEV-LAPTOP",
            entries).Value;
    }

    private static FileEntry File(ContentHash hash, long size, bool executable, DateTimeOffset? mtime = null) =>
        FileEntry.Create(hash, size, mtime ?? DateTimeOffset.UnixEpoch, executable).Value;

    private static Revision Revision(long value) => Domain.Manifests.Revision.TryCreate(value, out Revision? revision) ? revision : throw new ArgumentOutOfRangeException(nameof(value));

    private static ShareId Parse(string id) => ShareId.TryParse(id, out ShareId? shareId) ? shareId : throw new ArgumentException(id, nameof(id));

    private static SharePath P(string path) => ManifestBuilder.PathOf(path);
}
