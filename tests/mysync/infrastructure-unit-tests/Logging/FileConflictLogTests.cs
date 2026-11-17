using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.State;
using MySync.Domain.Sync;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.Logging;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Logging;

/// <summary><c>.mysynctool.log</c> (CLAUDE.md §7).</summary>
public sealed class FileConflictLogTests : IDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 16, 9, 2, 55, TimeSpan.Zero);

    private readonly TempShareFolder _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Detected_Should_WriteOneLine_WithKindPathBothSidesAndWhoChangedTheRemote()
    {
        FileConflictLog log = new(new ShareRoot(_temp.Path));
        ContentHash local = ManifestBuilder.HashOf("local");
        ContentHash remote = ManifestBuilder.HashOf("remote");

        (await log.RecordDetectedAsync(Conflict("reports/q3.xlsx", File(local), File(remote)), Ct)).IsSuccess.ShouldBeTrue();
        (await log.RecordDetectedAsync(Conflict("gone.txt", File(local), null, ConflictKind.ModifyDelete), Ct)).IsSuccess.ShouldBeTrue();

        Lines().ShouldBe([
            $"2026-11-16T09:02:55Z CONFLICT modify/modify reports/q3.xlsx local={local.Value[..12]}… remote={remote.Value[..12]}… (rev 18, ali@SERVER01)",
            $"2026-11-16T09:02:55Z CONFLICT modify/delete gone.txt local={local.Value[..12]}… remote=deleted (rev 18, ali@SERVER01)",
        ]);
    }

    [Fact]
    public async Task Resolved_Should_SayWhichSideWasKept()
    {
        FileConflictLog log = new(new ShareRoot(_temp.Path));

        await log.RecordResolvedAsync(ManifestBuilder.PathOf("a.txt"), KeepSide.Local, Seen, Ct);
        await log.RecordResolvedAsync(ManifestBuilder.PathOf("b.txt"), KeepSide.Remote, Seen, Ct);

        Lines().ShouldBe(["2026-11-16T09:02:55Z RESOLVED keep-local a.txt", "2026-11-16T09:02:55Z RESOLVED keep-remote b.txt"]);
    }

    [Fact]
    public async Task ACraftedUpdatedBy_Should_NotBeAbleToForgeALine()
    {
        FileConflictLog log = new(new ShareRoot(_temp.Path));

        await log.RecordDetectedAsync(
            Conflict("x.txt", null, null) with { RemoteUpdatedBy = "evil\n2026-01-01T00:00:00Z RESOLVED keep-local x.txt\u001b[2J" }, Ct);

        string line = Lines().Single();
        line.ShouldContain("evil\\u000A2026");
        line.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task TheLog_Should_RotateOnce_KeepingOneOldFile()
    {
        FileConflictLog log = new(new ShareRoot(_temp.Path), maxBytes: 300);

        for (int i = 0; i < 10; i++)
        {
            await log.RecordResolvedAsync(ManifestBuilder.PathOf($"file-{i}.txt"), KeepSide.Local, Seen, Ct);
        }

        new FileInfo(_temp.Combine(StateFileNames.ConflictLog)).Length.ShouldBeLessThanOrEqualTo(300);
        _temp.ReadText(StateFileNames.RotatedConflictLog).ShouldNotBeEmpty();
        Lines()[^1].ShouldEndWith("file-9.txt");
        Directory.GetFiles(_temp.Path).Length.ShouldBe(2);
    }

    public void Dispose() => _temp.Dispose();

    private string[] Lines() => _temp.ReadText(StateFileNames.ConflictLog).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static Conflict Conflict(string path, ManifestEntry? local, ManifestEntry? remote, ConflictKind kind = ConflictKind.ModifyModify)
    {
        Revision.TryCreate(18, out Revision? revision).ShouldBeTrue();
        return new Conflict(ManifestBuilder.PathOf(path), kind, null, local, remote, revision, "ali@SERVER01", Seen, Seen);
    }

    private static FileEntry File(ContentHash hash) => FileEntry.Create(hash, 1, Seen, executable: false).Value;
}
