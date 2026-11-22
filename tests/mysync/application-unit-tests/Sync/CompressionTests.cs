using System.IO.Compression;
using System.Text;
using MySync.Application.Conflicts.Diff;
using MySync.Application.Maintenance.CollectGarbage;
using MySync.Application.Shares.Compression;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Sync;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Sync;

/// <summary>
/// Share compression (CLAUDE.md §6, §9): how an object is stored, never what a file is. Every
/// machine reads either form; hashes, sizes, the merge and conflicts are all uncompressed.
/// </summary>
public sealed class CompressionTests
{
    private static readonly string Log = string.Concat(Enumerable.Range(0, 400).Select(i => $"2026-11-22T10:00:{i % 60:D2}Z INFO request {i} served\n"));

    [Fact]
    public async Task ACompressedShare_Should_StoreTextGzip_AndEveryMachineReadItBack_ByteForByte()
    {
        SyncWorld world = new();
        Machine a = world.Machine("usman@DEV-LAPTOP");
        a.Folder.WriteFile("logs/app.log", Log).WriteFile("tiny.txt", "small").WriteFile("data.zip", "zip " + Log);
        CreateShareResponse created = (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();

        created.Compression.ShouldBe(CompressionSetting.Gzip);
        created.Uploaded.Compressed.ShouldBeTrue();
        created.Uploaded.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(Log) * 2 + 4 + 5);
        created.Uploaded.StoredBytes.ShouldBeLessThan(created.Uploaded.Bytes);
        (await world.HeadAsync(created.ShareId)).FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        EncodingOf(world, created.ShareId, Log).ShouldBe("gzip");
        EncodingOf(world, created.ShareId, "small").ShouldBeNull(); // under 512 bytes
        EncodingOf(world, created.ShareId, "zip " + Log).ShouldBeNull(); // already compressed, by its name

        Machine b = world.Machine("ali@SERVER01");
        (await b.CloneAsync(created.ShareId)).ShouldSucceed();
        b.Files().ShouldBe(a.Files());

        b.Folder.WriteFile("logs/app.log", Log + "one more line\n");
        (await b.SyncAsync()).ShouldSucceed().Uploaded.Compressed.ShouldBeTrue();
        (await a.SyncAsync()).ShouldSucceed();
        a.Folder.ReadBytes("logs/app.log").ShouldBe(b.Folder.ReadBytes("logs/app.log"));
    }

    [Fact]
    public async Task TheStoredForm_Should_FollowThePolicy()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        byte[] noise = new byte[8192];
        System.Security.Cryptography.RandomNumberGenerator.Fill(noise);
        a.Folder.WriteFile("server.log", Log).WriteFile("photo.jpg", "jpg " + Log).WriteFile("noise.bin", noise).WriteFile("note.txt", "short");

        CreateShareResponse created = (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();

        EncodingOf(world, created.ShareId, Log).ShouldBe("gzip");
        EncodingOf(world, created.ShareId, "jpg " + Log).ShouldBeNull();
        world.Store.ContentEncodingOf(created.ShareId, ManifestBuilder.HashOf(noise)).ShouldBeNull();
        EncodingOf(world, created.ShareId, "short").ShouldBeNull();
    }

    [Fact]
    public async Task AnAlreadyCompressedType_Should_StayRaw()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("backup.zip", Log);

        CreateShareResponse created = (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();

        EncodingOf(world, created.ShareId, Log).ShouldBeNull();
        created.Uploaded.Compressed.ShouldBeFalse();
    }

    [Fact]
    public async Task ConflictsDiffAndResolve_Should_WorkOnUncompressedContent()
    {
        (SyncWorld world, Machine a, Machine b, _) = await CompressedTwoMachines(("report.csv", Log));
        a.Folder.WriteFile("report.csv", Log.Replace("request 7 ", "request 7a ", StringComparison.Ordinal));
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("report.csv", Log.Replace("request 9 ", "request 9b ", StringComparison.Ordinal));
        (await b.SyncAsync()).ShouldSucceed().Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);

        PathDiff diff = (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single();
        TextOutcome text = diff.Sections.Single().Outcome.ShouldBeOfType<TextOutcome>();
        (text.Added, text.Removed).ShouldBe((2, 2));
        await using (Stream remote = (await b.PrintAsync("report.csv", DiffSideKind.Remote)).ShouldSucceed())
        {
            using StreamReader reader = new(remote);
            (await reader.ReadToEndAsync(SyncWorld.Ct)).ShouldBe(a.Folder.ReadText("report.csv"));
        }

        (await b.ResolveAsync("remote", "report.csv")).ShouldSucceed();
        b.Folder.ReadText("report.csv").ShouldBe(a.Folder.ReadText("report.csv"));

        b.Folder.WriteFile("report.csv", "b decided\n" + Log);
        (await b.SyncAsync()).ShouldSucceed();
        a.Folder.WriteFile("report.csv", "a again\n" + Log);
        (await a.SyncAsync()).ShouldSucceed().Conflicts.ShouldNotBeEmpty();
        (await a.ResolveAsync("local", "report.csv")).ShouldSucceed();
        (await b.SyncAsync()).ShouldSucceed();
        b.Folder.ReadText("report.csv").ShouldBe("a again\n" + Log);
        world.ShouldNotBeNull();
    }

    [Fact]
    public async Task ABinaryDiff_Should_SayBinary_OnACompressedShare()
    {
        byte[] binary = [0, 1, 2, .. Encoding.UTF8.GetBytes(Log)];
        (_, Machine a, Machine b, _) = await CompressedTwoMachines(("tool.bin", Encoding.UTF8.GetString(binary)));
        a.Folder.WriteFile("tool.bin", [.. binary, 9]);
        (await a.SyncAsync()).ShouldSucceed();
        b.Folder.WriteFile("tool.bin", [.. binary, 8]);
        (await b.SyncAsync()).ShouldSucceed();

        (await b.DiffAsync(new DiffQuery([]))).ShouldSucceed().Items.Single().Sections.Single().Outcome.ShouldBeOfType<BinaryOutcome>();
    }

    [Fact]
    public async Task GcAndDelete_Should_WorkOnACompressedShare()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await CompressedTwoMachines(("a.log", Log));
        a.Folder.WriteFile("a.log", Log + "edit\n");
        (await a.SyncAsync()).ShouldSucceed();
        world.Clock.Advance(TimeSpan.FromDays(3));

        GarbageReport gc = (await a.GcAsync(new CollectGarbageCommand(KeepRevisions: 1))).ShouldSucceed();
        gc.DeletedObjects.Single().Encoding.ShouldBe(ObjectEncoding.Gzip);
        (await a.DeleteAsync()).ShouldSucceed();
        world.Store.BlobNames.Where(name => name.StartsWith($"shares/{shareId}/objects/", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TurningCompressionOnAndOff_Should_MixForms_AndNeverRewriteAnObject()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await RawTwoMachines(("old.log", Log));
        EncodingOf(world, shareId, Log).ShouldBeNull();

        CompressionResponse on = (await a.SetCompressionAsync(true)).ShouldSucceed();
        on.Changed.ShouldBeTrue();
        on.FormatVersion.ShouldBe(3);
        SyncReport unchanged = (await a.SyncAsync()).ShouldSucceed();
        unchanged.Uploaded.Files.ShouldBe(0); // turning it on re-uploads nothing
        EncodingOf(world, shareId, Log).ShouldBeNull();

        string newer = Log + "newer\n";
        a.Folder.WriteFile("new.log", newer);
        (await a.SyncAsync()).ShouldSucceed();
        EncodingOf(world, shareId, newer).ShouldBe("gzip");

        (await a.SetCompressionAsync(false)).ShouldSucceed().ShouldSatisfyAllConditions(
            off => off.Setting.ShouldBe(CompressionSetting.None),
            off => off.FormatVersion.ShouldBe(3));
        string newest = Log + "newest\n";
        a.Folder.WriteFile("newest.log", newest);
        (await a.SyncAsync()).ShouldSucceed();
        EncodingOf(world, shareId, newest).ShouldBeNull();
        EncodingOf(world, shareId, newer).ShouldBe("gzip");

        (await b.SyncAsync()).ShouldSucceed();
        b.Files().ShouldBe(a.Files());
        Machine c = world.Machine("c");
        (await c.CloneAsync(shareId)).ShouldSucceed();
        c.Files().ShouldBe(a.Files());
    }

    [Fact]
    public async Task SetCompression_Should_DoNothing_WhenAlreadySo()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await RawTwoMachines(("a.txt", "a"));
        Revision before = (await world.HeadAsync(shareId)).Revision;

        CompressionResponse off = (await a.SetCompressionAsync(false)).ShouldSucceed();

        off.Changed.ShouldBeFalse();
        off.Revision.ShouldBe(before);
        (await world.HeadAsync(shareId)).FormatVersion.ShouldBe(1);
        (await a.CompressionAsync()).ShouldSucceed().Setting.ShouldBe(CompressionSetting.None);
    }

    [Fact]
    public async Task SetCompression_Should_RetryWhenSomeoneElseCommitsFirst_AndKeepTheirChange()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await RawTwoMachines(("a.txt", "a"));
        bool once = false;
        world.Store.BeforeReplaceManifest = async id =>
        {
            if (!once)
            {
                once = true;
                await world.CommitElsewhereAsync(id);
            }
        };

        CompressionResponse on = (await a.SetCompressionAsync(true)).ShouldSucceed();

        Manifest head = await world.HeadAsync(shareId);
        head.Compression.ShouldBe(CompressionSetting.Gzip);
        head.Revision.ShouldBe(on.Revision);
        head.Revision.Value.ShouldBe(3); // create, the other machine's commit, then ours
    }

    [Fact]
    public async Task EveryCommitPath_Should_CarryTheSettingForward()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await CompressedTwoMachines(("x.txt", Log));
        a.Folder.WriteFile("x.txt", Log + "a\n");
        (await a.SyncAsync()).ShouldSucceed();
        (await world.HeadAsync(shareId)).Compression.ShouldBe(CompressionSetting.Gzip);

        b.Folder.WriteFile("x.txt", Log + "b\n");
        (await b.SyncAsync()).ShouldSucceed();
        (await b.ResolveAsync("local", "x.txt")).ShouldSucceed().Committed.ShouldBeTrue();
        (await world.HeadAsync(shareId)).ShouldSatisfyAllConditions(
            head => head.Compression.ShouldBe(CompressionSetting.Gzip),
            head => head.FormatVersion.ShouldBe(3));

        (await a.RenameAsync("renamed-share")).ShouldSucceed();
        (await world.HeadAsync(shareId)).Compression.ShouldBe(CompressionSetting.Gzip);
    }

    [Fact]
    public async Task TheSameContent_FromTwoMachinesDisagreeingAboutCompression_Should_BeOneObject()
    {
        // B's sync reads HEAD with compression off and uploads raw; before it commits, A turns
        // compression on and syncs the same content: A finds the object stored and skips it.
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await RawTwoMachines(("base.txt", "b"));
        a.Folder.WriteFile("same.log", Log);
        b.Folder.WriteFile("same.log", Log);
        bool once = false;
        world.Store.BeforeReplaceManifest = async _ =>
        {
            if (!once)
            {
                once = true;
                (await a.SetCompressionAsync(true)).ShouldSucceed();
                (await a.SyncAsync()).ShouldSucceed().Uploaded.Files.ShouldBe(0);
            }
        };

        (await b.SyncAsync()).ShouldSucceed();

        world.Store.BlobNames.Count(name => name.StartsWith($"shares/{shareId}/objects/", StringComparison.Ordinal)).ShouldBe(2);
        EncodingOf(world, shareId, Log).ShouldBeNull();
        Manifest head = await world.HeadAsync(shareId);
        head.Compression.ShouldBe(CompressionSetting.Gzip); // B's retry carried A's setting forward
        Machine c = world.Machine("c");
        (await c.CloneAsync(shareId)).ShouldSucceed();
        c.Folder.ReadText("same.log").ShouldBe(Log);
    }

    [Fact]
    public async Task AFileThatChangesWhileItIsCompressed_Should_BeSkipped_AndNothingStoredUnderItsHash()
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await CompressedTwoMachines(("base.txt", "b"));
        string scanned = Log + "scanned\n";
        a.Folder.WriteFile("busy.log", scanned);
        a.Folder.ReadStream = (path, bytes) => path.Value == "busy.log"
            ? new MemoryStream(Encoding.UTF8.GetBytes(Log + "changed while read\n"))
            : new MemoryStream(bytes);

        SyncReport report = (await a.SyncAsync()).ShouldSucceed();

        report.Skipped.Single().ShouldSatisfyAllConditions(
            skip => skip.Path.ShouldBe("busy.log"),
            skip => skip.Cause.ShouldBe(SkipCause.Unstable));
        (await world.Store.ObjectExistsAsync(shareId, ManifestBuilder.HashOf(scanned), SyncWorld.Ct)).Value.ShouldBeFalse();
        (await world.HeadAsync(shareId)).Entries.Keys.Select(path => path.Value).ShouldNotContain("busy.log");
    }

    public static TheoryData<string, string> Damage => new()
    {
        { "not gzip at all", "invalid gzip" },
        { "truncated", "match its hash" },
        { "too long", "more than" },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task ADamagedObject_Should_SkipThatPath_WriteNothing_AndLetTheRestSync(string damage, string detail)
    {
        (SyncWorld world, Machine a, _, ShareId shareId) = await CompressedTwoMachines(("ok.log", Log + "ok\n"));
        a.Folder.WriteFile("bad.log", Log);
        (await a.SyncAsync()).ShouldSucceed();
        byte[] gzip = Gzip(Encoding.UTF8.GetBytes(Log));
        byte[] stored = damage switch
        {
            "not gzip at all" => Encoding.UTF8.GetBytes("this was never gzip but says it is"),
            "truncated" => gzip[..(gzip.Length / 2)],
            _ => Gzip(Encoding.UTF8.GetBytes(Log + Log)),
        };
        world.Store.PutObjectAsStored(shareId, ManifestBuilder.HashOf(Log), stored, "gzip");

        Machine c = world.Machine("c");
        SyncReport cloned = (await c.CloneAsync(shareId)).ShouldSucceed();

        cloned.Skipped.Single().ShouldSatisfyAllConditions(
            skip => skip.Path.ShouldBe("bad.log"),
            skip => skip.Cause.ShouldBe(SkipCause.ApplyFailed),
            skip => skip.Detail.ShouldContain(detail));
        c.Folder.AllPaths.ShouldNotContain("bad.log");
        c.Folder.ReadText("ok.log").ShouldBe(Log + "ok\n");
    }

    [Fact]
    public async Task AnUnknownEncoding_Should_SayUpgradeMysync()
    {
        (SyncWorld world, Machine a, Machine b, ShareId shareId) = await CompressedTwoMachines(("base.txt", "b"));
        a.Folder.WriteFile("new.log", Log);
        (await a.SyncAsync()).ShouldSucceed();
        world.Store.PutObjectAsStored(shareId, ManifestBuilder.HashOf(Log), Encoding.UTF8.GetBytes("x"), "br");

        SyncReport report = (await b.SyncAsync()).ShouldSucceed();
        report.Skipped.Single().Detail.ShouldContain("Upgrade mysync");
        (await b.PrintAsync("new.log", DiffSideKind.Remote)).ShouldFail().Code.ShouldBe("Storage.UnsupportedEncoding");
    }

    [Fact]
    public async Task Publishing_Should_KeepGzipObjectsGzip_WithTheirHeader_AndRawOnesRaw()
    {
        SyncWorld world = new();
        Machine a = world.Machine("a");
        a.Folder.WriteFile("server.log", Log).WriteFile("note.txt", "short");
        (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();

        MySync.Application.Publishing.Publish.PublishReport report = (await a.PublishAsync()).ShouldSucceed();

        string prefix = $"p/{report.PublishId}/files/";
        StoredBlob log = world.Publishing.Blob(prefix + "server.log")!;
        log.Headers.ContentEncoding.ShouldBe("gzip");
        Gunzip(log.Bytes).ShouldBe(Encoding.UTF8.GetBytes(Log));
        StoredBlob note = world.Publishing.Blob(prefix + "note.txt")!;
        note.Headers.ContentEncoding.ShouldBeNull();
        note.Bytes.ShouldBe(Encoding.UTF8.GetBytes("short"));
        // The checksums people verify against are the real (uncompressed) content's.
        string sums = Encoding.UTF8.GetString(world.Publishing.Blob($"p/{report.PublishId}/SHA256SUMS.txt")!.Bytes);
        sums.ShouldContain($"{ManifestBuilder.HashOf(Log).Value}  server.log");
        world.Publishing.Blob($"p/{report.PublishId}/site-data.js").ShouldNotBeNull();
    }

    [Fact]
    public async Task PublishingThroughThisMachine_Should_DecodeOnce_AndPublishRaw()
    {
        SyncWorld world = new();
        world.Store.CanMakeReadUrls = false;
        Machine a = world.Machine("a");
        a.Folder.WriteFile("server.log", Log);
        (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();

        MySync.Application.Publishing.Publish.PublishReport report = (await a.PublishAsync()).ShouldSucceed();

        StoredBlob log = world.Publishing.Blob($"p/{report.PublishId}/files/server.log")!;
        log.Headers.ContentEncoding.ShouldBeNull();
        log.Bytes.ShouldBe(Encoding.UTF8.GetBytes(Log));
    }

    private static string? EncodingOf(SyncWorld world, ShareId shareId, string content) =>
        world.Store.ContentEncodingOf(shareId, ManifestBuilder.HashOf(content));

    private static async Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> CompressedTwoMachines(params (string Path, string Content)[] files)
    {
        SyncWorld world = new();
        Machine a = world.Machine("usman@DEV-LAPTOP");
        foreach ((string path, string content) in files)
        {
            a.Folder.WriteFile(path, content);
        }

        CreateShareResponse created = (await a.CreateAsync(new CreateShareCommand(Compress: true))).ShouldSucceed();
        Machine b = world.Machine("ali@SERVER01");
        (await b.CloneAsync(created.ShareId)).ShouldSucceed();
        return (world, a, b, created.ShareId);
    }

    private static Task<(SyncWorld World, Machine A, Machine B, ShareId ShareId)> RawTwoMachines(params (string Path, string Content)[] files) =>
        RunSyncTests.TwoMachines(files);

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using GZipStream gzip = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
