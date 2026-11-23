using System.Text;
using MySync.Domain.Sync.Push;
using MySync.Infrastructure.Serialization;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Serialization;

/// <summary><c>_mysync-push.json</c>: the exact bytes are pinned, so the same push always stores the same object.</summary>
public class PushRecordSerializerTests
{
    [Fact]
    public void Record_Should_SerializeToThisExactJson()
    {
        PushRecord record = new(
            "web01",
            "svc-deploy",
            "Ubuntu 24.04.1 LTS (linux-x64)",
            "0.8.0",
            new DateTimeOffset(2026, 11, 21, 5, 12, 7, 345, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 21, 7, 13, 41, TimeSpan.FromHours(2)),
            ["/var/log/app", "/etc/app/config.yml"],
            "web01/20261121-051207Z",
            214,
            1395864371,
            [new PushRecordSkip("app/current.lock", "unreadable", "it is locked by another process")]);

        string json = Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record));

        json.ShouldBe("""
            {
              "formatVersion": 1,
              "host": "web01",
              "user": "svc-deploy",
              "os": "Ubuntu 24.04.1 LTS (linux-x64)",
              "mysyncVersion": "0.8.0",
              "startedUtc": "2026-11-21T05:12:07Z",
              "finishedUtc": "2026-11-21T05:13:41Z",
              "sources": [
                "/var/log/app",
                "/etc/app/config.yml"
              ],
              "target": "web01/20261121-051207Z",
              "files": 214,
              "bytes": 1395864371,
              "skipped": [
                {
                  "path": "app/current.lock",
                  "cause": "unreadable",
                  "detail": "it is locked by another process"
                }
              ]
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AFilteredRecord_Should_AddTheFiltersAndCounts_AfterEverythingElse()
    {
        PushRecord record = new(
            "web01",
            "svc-deploy",
            "Ubuntu 24.04.1 LTS (linux-x64)",
            "0.9.0",
            new DateTimeOffset(2026, 11, 23, 0, 45, 12, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 23, 0, 45, 30, TimeSpan.Zero),
            ["/var/log/app"],
            "web01/20261123-004512Z",
            12,
            88184422,
            [],
            new PushFilterSettings(new DateTimeOffset(2026, 11, 22, 23, 45, 12, 250, TimeSpan.Zero), "last", ["*.log"], ["*.gz"], 524288000),
            new PushFilteredOut(NotChanged: 188, NotIncluded: 0, Excluded: 14, TooLarge: 1));

        string json = Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record));

        json.ShouldBe("""
            {
              "formatVersion": 1,
              "host": "web01",
              "user": "svc-deploy",
              "os": "Ubuntu 24.04.1 LTS (linux-x64)",
              "mysyncVersion": "0.9.0",
              "startedUtc": "2026-11-23T00:45:12Z",
              "finishedUtc": "2026-11-23T00:45:30Z",
              "sources": [
                "/var/log/app"
              ],
              "target": "web01/20261123-004512Z",
              "files": 12,
              "bytes": 88184422,
              "skipped": [],
              "filters": {
                "updatedSince": "2026-11-22T23:45:12Z",
                "updatedSinceSpec": "last",
                "include": [
                  "*.log"
                ],
                "exclude": [
                  "*.gz"
                ],
                "maxSizeBytes": 524288000
              },
              "filteredOut": {
                "total": 203,
                "notChanged": 188,
                "notIncluded": 0,
                "excluded": 14,
                "tooLarge": 1
              }
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AFilterWithoutACutoff_Should_WriteNulls()
    {
        PushRecord record = new("h", "u", "os", "1.0.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], "h/19700101-000000Z", 0, 0, [],
            new PushFilterSettings(null, "last", [], [], null), PushFilteredOut.None);

        string json = Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record));

        json.ShouldContain("\"updatedSince\": null");
        json.ShouldContain("\"maxSizeBytes\": null");
        json.ShouldContain("\"total\": 0");
    }

    [Fact]
    public void AnUnfilteredRecord_Should_BeExactlyWhatItWasBeforeFilters()
    {
        PushRecord record = new("h", "u", "os", "1.0.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ["/x"], "h/19700101-000000Z", 1, 2, []);

        string json = Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record));

        json.ShouldNotContain("filter");
        json.ShouldEndWith("  \"skipped\": []\n}\n");

        // Counts without settings are never written either.
        Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record with { FilteredOut = PushFilteredOut.None })).ShouldBe(json);
    }

    [Fact]
    public void Text_Should_BeEscaped_SoNoNameCanBreakTheJson()
    {
        PushRecord record = new("h", "u", "os", "1.0.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ["/x/<a>\"b\""], "h/19700101-000000Z", 0, 0, []);

        string json = Encoding.UTF8.GetString(new PushRecordSerializer().Serialize(record));

        static string Escaped(string hex) => string.Concat("\\", "u", hex);

        json.ShouldContain($"\"/x/{Escaped("003C")}a{Escaped("003E")}{Escaped("0022")}b{Escaped("0022")}\"");
    }

    [Fact]
    public void TheSameRecord_Should_GiveTheSameBytes()
    {
        PushRecord record = new("h", "u", "os", "1.0.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], "h/19700101-000000Z", 0, 0, []);

        new PushRecordSerializer().Serialize(record).ShouldBe(new PushRecordSerializer().Serialize(record));
    }
}
