using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MySync.Application.Shares.Create;
using MySync.Application.Sync.Run;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Domain.Sync;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.AzureMachine;

namespace MySync.EndToEndTests;

/// <summary>
/// A compressed share on Azurite with the full DI graph (CLAUDE.md §6, §9): text stored gzip, an
/// already-compressed file raw, both machines always byte-identical, through edits, a conflict and
/// its resolution, and compression turned off again.
/// </summary>
public sealed class CompressedShareTests : IDisposable
{
    private readonly AzuriteFixture _azurite;
    private readonly AzureMachine _a;
    private readonly AzureMachine _b;
    private readonly string _container = AzuriteEnvironment.NewContainerName();

    public CompressedShareTests(AzuriteFixture azurite)
    {
        _azurite = azurite;
        _a = new AzureMachine(azurite.ConnectionString, _container);
        _b = new AzureMachine(azurite.ConnectionString, _container);
    }

    [Fact]
    public async Task ACompressedShare_Should_StayByteIdentical_OnBothMachines_ThroughEditsConflictsAndTurningItOff()
    {
        byte[] log = BigLog(16 * 1024 * 1024); // ~2x compressible: the gzip upload spans several 4 MiB blocks
        byte[] zip = RandomBytes(300_000);
        _a.Folder.WriteFile("logs/app.log", log).WriteFile("dist/bundle.zip", zip).WriteFile("notes.md", "# notes\n" + new string('n', 2000));

        CreateShareResponse created = Ok(await _a.CreateCompressedAsync());
        ShareId shareId = created.ShareId;
        created.Compression.ShouldBe(CompressionSetting.Gzip);
        created.Uploaded.StoredBytes.ShouldBeLessThan(created.Uploaded.Bytes);

        BlobProperties logBlob = await PropertiesAsync(shareId, log);
        logBlob.ContentEncoding.ShouldBe("gzip");
        logBlob.ContentLength.ShouldBeGreaterThan(4L * 1024 * 1024);
        logBlob.ContentLength.ShouldBeLessThan(log.LongLength);
        logBlob.Metadata["size"].ShouldBe(log.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await PropertiesAsync(shareId, zip)).ContentEncoding.ShouldBeNull();

        Ok(await _b.CloneAsync(shareId));
        ShouldBeIdentical();

        // Both directions.
        byte[] longer = [.. log, .. Encoding.UTF8.GetBytes("appended on b\n")];
        _b.Folder.WriteFile("logs/app.log", longer);
        Ok(await _b.SyncAsync()).Uploaded.Compressed.ShouldBeTrue();
        Ok(await _a.SyncAsync());
        ShouldBeIdentical();
        _a.Folder.WriteFile("notes.md", "# notes from a\n" + new string('a', 3000));
        Ok(await _a.SyncAsync());
        Ok(await _b.SyncAsync());
        ShouldBeIdentical();

        // A conflict, decided for the share's version.
        _a.Folder.WriteFile("notes.md", "# a's version\n" + new string('x', 3000));
        Ok(await _a.SyncAsync());
        _b.Folder.WriteFile("notes.md", "# b's version\n" + new string('y', 3000));
        Ok(await _b.SyncAsync()).Conflicts.Single().Kind.ShouldBe(ConflictKind.ModifyModify);
        Ok(await _b.ResolveAsync("remote", "notes.md")).Resolved.Length.ShouldBe(1);
        ShouldBeIdentical();

        // Off again: new uploads raw, what was stored compressed stays so and still reads.
        Ok(await _a.SetCompressionAsync(on: false)).FormatVersion.ShouldBe(Manifest.CompressionFormatVersion);
        byte[] later = BigLog(200_000);
        _a.Folder.WriteFile("logs/later.log", later);
        Ok(await _a.SyncAsync()).Uploaded.Compressed.ShouldBeFalse();
        Ok(await _b.SyncAsync());
        ShouldBeIdentical();
        (await PropertiesAsync(shareId, later)).ContentEncoding.ShouldBeNull();
        (await PropertiesAsync(shareId, longer)).ContentEncoding.ShouldBe("gzip");

        using AzureMachine c = new(_azurite.ConnectionString, _container);
        Ok(await c.CloneAsync(shareId));
        c.Folder.Files().ShouldBe(_a.Folder.Files());
    }

    private void ShouldBeIdentical()
    {
        IReadOnlyDictionary<string, string> a = _a.Folder.Files();
        a.ShouldBe(_b.Folder.Files());
        foreach (string path in a.Keys)
        {
            SHA256.HashData(File.ReadAllBytes(_b.Folder.Combine(path))).ShouldBe(SHA256.HashData(File.ReadAllBytes(_a.Folder.Combine(path))), path);
        }
    }

    private async Task<BlobProperties> PropertiesAsync(ShareId shareId, byte[] content)
    {
        BlobClient blob = new BlobContainerClient(_azurite.ConnectionString, _container)
            .GetBlobClient(ShareLayout.For(shareId).ObjectBlob(ManifestBuilder.HashOf(content)));
        return (await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken)).Value;
    }

    /// <summary>Log lines with a random hex id each: compresses to about half, not to nothing.</summary>
    private static byte[] BigLog(int length)
    {
        StringBuilder text = new(length + 128);
        byte[] id = new byte[16];
        int line = 0;
        while (text.Length < length)
        {
            RandomNumberGenerator.Fill(id);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"2026-11-22T10:{line % 60:D2}:00Z INFO request {Convert.ToHexString(id)} served\n");
            line++;
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}
