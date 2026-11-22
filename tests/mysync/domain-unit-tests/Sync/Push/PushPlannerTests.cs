using System.Collections.Immutable;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Sync.Push;

/// <summary>
/// A push only adds, under the first free target folder; every entry of HEAD is in the result
/// unchanged. The last test checks that property over generated manifests.
/// </summary>
public class PushPlannerTests
{
    private const string Other = "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";

    private static readonly DateTimeOffset Started = new(2026, 11, 21, 5, 12, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Started.AddMinutes(2);

    private static readonly PushTarget Web01 = PushTarget.Create("web01", Started).Value;

    private static readonly PushedEntry[] Logs =
    [
        new(Path("app/error.log"), File()),
        new(Path("app/old"), DirectoryEntry.Instance),
        new(Path("config.yml"), File(Other, 3)),
    ];

    private static Manifest Head(params KeyValuePair<SharePath, ManifestEntry>[] entries) =>
        Ok(Manifest.CreateNew(Share(), entries, Created, "laptop@DEV"));

    [Fact]
    public void IntoAnEmptyShare_Should_AddEverythingUnderTheTargetFolder()
    {
        PushPlan plan = Ok(PushPlanner.Plan(Head(), Logs, Web01, Now, "svc@web01"));

        plan.Folder.Value.ShouldBe("web01/20261121-051207Z");
        plan.Manifest.Entries.Keys.Select(path => path.Value).ShouldBe(
            ["web01/20261121-051207Z/app/error.log", "web01/20261121-051207Z/app/old", "web01/20261121-051207Z/config.yml"]);
        plan.Manifest.Revision.Value.ShouldBe(2);
        plan.Manifest.UpdatedBy.ShouldBe("svc@web01");
        plan.Manifest.UpdatedUtc.ShouldBe(Now);
    }

    [Fact]
    public void BesideOtherHostsAndSyncedFiles_Should_LeaveThemAlone()
    {
        Manifest head = Head(
            Entry("docs/readme.md", File()),
            Entry("db01/20261120-000000Z/x.log", File(Other, 3)),
            Entry("web01/20261120-000000Z/x.log", File()),
            Entry("empty", DirectoryEntry.Instance));

        PushPlan plan = Ok(PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01"));

        plan.Folder.Value.ShouldBe("web01/20261121-051207Z");
        foreach ((SharePath path, ManifestEntry entry) in head.Entries)
        {
            plan.Manifest.Entries[path].ShouldBe(entry);
        }

        plan.Manifest.Entries.Count.ShouldBe(head.Entries.Count + Logs.Length);
    }

    [Fact]
    public void AnOccupiedTargetFolder_Should_GetTheNextSuffix()
    {
        Manifest head = Head(
            Entry("web01/20261121-051207Z/a", File()),
            Entry("web01/20261121-051207Z-2", DirectoryEntry.Instance), // an empty folder counts as taken
            Entry("web01/20261121-051207Z-3", File())); // and so does a file of that name

        Ok(PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01")).Folder.Value.ShouldBe("web01/20261121-051207Z-4");
    }

    [Fact]
    public void AFolderWithALongerNameStartingTheSame_Should_NotTakeTheTarget()
    {
        Manifest head = Head(Entry("web01/20261121-051207Z-old/a", File()), Entry("web01x/20261121-051207Z/a", File()));

        Ok(PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01")).Folder.Value.ShouldBe("web01/20261121-051207Z");
    }

    [Fact]
    public void AFileNamedLikeTheHost_Should_BeTargetIsAFile()
    {
        Manifest head = Head(Entry("web01", File()));

        SharedKernel.Error error = PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01").Error;

        error.Code.ShouldBe("Push.TargetIsAFile");
        error.Description.ShouldContain("--host");
    }

    [Fact]
    public void AnEmptyHostFolder_Should_BeUsed_ItsDirEntryBecomingImplied()
    {
        Manifest head = Head(Entry("web01", DirectoryEntry.Instance));

        PushPlan plan = Ok(PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01"));

        plan.Folder.Value.ShouldBe("web01/20261121-051207Z");
        plan.Manifest.Entries.ContainsKey(Path("web01")).ShouldBeFalse(); // no longer empty
    }

    [Fact]
    public void ACompressedShare_Should_StayCompressed()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [], Created, "laptop@DEV", CompressionSetting.Gzip));

        Manifest next = Ok(PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01")).Manifest;

        next.Compression.ShouldBe(CompressionSetting.Gzip);
        next.FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
    }

    [Fact]
    public void ADeletedShare_Should_BeRefused()
    {
        Manifest deleted = Ok(Head(Entry("a", File())).CreateDeletedRevision(Now, "x@y"));

        PushPlanner.Plan(deleted, Logs, Web01, Now, "svc@web01").Error.Code.ShouldBe("Shares.Deleted");
    }

    [Fact]
    public void Place_Should_PutEachEntryUnderTheFolder()
    {
        PushPlanner.Place(Logs, Path("h/t")).Select(pair => pair.Key.Value).ShouldBe(["h/t/app/error.log", "h/t/app/old", "h/t/config.yml"]);
    }

    /// <summary>Whatever HEAD holds, nothing of it changes or disappears, and everything pushed is added.</summary>
    [Fact]
    public void GeneratedManifests_Should_KeepEveryHeadEntryUnchanged()
    {
        string[] names = ["web01", "db01", "docs", "a", "20261121-051207Z", "20261121-051207Z-2", "x.log"];
        SplitMix random = new(20261121);

        for (int run = 0; run < 400; run++)
        {
            Dictionary<string, ManifestEntry> entries = new(StringComparer.Ordinal);
            int count = random.Next(0, 25);
            for (int i = 0; i < count; i++)
            {
                int depth = random.Next(1, 4);
                string path = string.Join('/', Enumerable.Range(0, depth).Select(_ => names[random.Next(names.Length)]));
                bool insideAFile = entries.Keys.Any(existing => path.StartsWith(existing + "/", StringComparison.Ordinal) && entries[existing] is FileEntry)
                    || entries.Keys.Any(existing => existing.StartsWith(path + "/", StringComparison.Ordinal));
                if (!insideAFile && !entries.ContainsKey(path))
                {
                    string hash = random.Next(2) == 0 ? HashOfTest : Other;
                    entries[path] = random.Next(4) == 0 ? DirectoryEntry.Instance : File(hash);
                }
            }

            Manifest head = Head([.. entries.Select(pair => Entry(pair.Key, pair.Value))]);
            SharedKernel.Result<PushPlan> planned = PushPlanner.Plan(head, Logs, Web01, Now, "svc@web01");

            if (head.Entries.TryGetValue(Path("web01"), out ManifestEntry? atHost) && atHost is FileEntry)
            {
                planned.Error.Code.ShouldBe("Push.TargetIsAFile");
                continue;
            }

            PushPlan plan = Ok(planned);
            ImmutableSortedDictionary<SharePath, ManifestEntry> result = plan.Manifest.Entries;
            foreach ((SharePath path, ManifestEntry entry) in head.Entries)
            {
                bool impliedHostDirectory = entry is DirectoryEntry && plan.Folder.IsUnder(path);
                if (!impliedHostDirectory)
                {
                    result.ShouldContainKey(path, $"run {run}");
                    result[path].ShouldBe(entry, $"run {run}: {path}");
                }
            }

            foreach ((SharePath path, ManifestEntry entry) in PushPlanner.Place(Logs, plan.Folder))
            {
                result[path].ShouldBe(entry);
                head.Entries.ContainsKey(path).ShouldBeFalse();
            }

            head.Entries.Keys.ShouldNotContain(path => path == plan.Folder || path.IsUnder(plan.Folder), $"run {run}: the folder was not free");
        }
    }

    /// <summary>A small fixed-seed generator (SplitMix64), so the data is the same on every run and OS.</summary>
    private sealed class SplitMix(int seed)
    {
        private ulong _state = (ulong)seed;

        public int Next(int maxExclusive) => Next(0, maxExclusive);

        public int Next(int minInclusive, int maxExclusive)
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(z % (ulong)(maxExclusive - minInclusive));
        }
    }
}
