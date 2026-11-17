using System.Security.Cryptography;
using MySync.Application.Sync.Run;
using MySync.Domain.Shares;
using MySync.Testing;
using Shouldly;
using Xunit;
using static MySync.EndToEndTests.AzureMachine;

namespace MySync.EndToEndTests;

/// <summary>
/// Sizes Azurite can't tell us much about: a 300 MB file and a 5,000-file share. Off unless
/// <see cref="Variable"/> is <c>1</c>; the manually triggered real-Azure workflow turns them on
/// against a test-only storage account (CLAUDE.md §14).
/// </summary>
public sealed class LargeShareTests : IDisposable
{
    public const string Variable = "MYSYNC_TEST_LARGE";

    private readonly AzureMachine _a;
    private readonly AzureMachine _b;

    public LargeShareTests(AzuriteFixture azurite)
    {
        ArgumentNullException.ThrowIfNull(azurite);
        string container = AzuriteEnvironment.NewContainerName();
        _a = new AzureMachine(azurite.ConnectionString, container);
        _b = new AzureMachine(azurite.ConnectionString, container);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A300MegabyteFile_Should_RoundTrip_AndAnEditShouldSyncBack()
    {
        SkipUnlessEnabled();
        const long Size = 300L * 1024 * 1024;
        string written = await WriteRandomAsync(_a.Folder.Combine("big.bin"), Size);

        ShareId shareId = await _a.CreateAsync();
        SyncReport cloned = Ok(await _b.CloneAsync(shareId));

        cloned.Downloaded.Bytes.ShouldBe(Size);
        (await HashAsync(_b.Folder.Combine("big.bin"))).ShouldBe(written);

        string edited = await WriteRandomAsync(_b.Folder.Combine("big.bin"), Size + 1);
        Ok(await _b.SyncAsync()).Uploaded.Bytes.ShouldBe(Size + 1);
        Ok(await _a.SyncAsync()).Downloaded.Bytes.ShouldBe(Size + 1);
        (await HashAsync(_a.Folder.Combine("big.bin"))).ShouldBe(edited);
    }

    [Fact]
    public async Task AFiveThousandFileShare_Should_CloneAndSyncAHundredEdits()
    {
        SkipUnlessEnabled();
        foreach (int i in Enumerable.Range(0, 5000))
        {
            _a.Folder.WriteFile($"d{i % 50:D2}/f{i:D4}.txt", $"file {i}");
        }

        ShareId shareId = await _a.CreateAsync();
        Ok(await _b.CloneAsync(shareId)).Downloaded.Files.ShouldBe(5000);

        foreach (int i in Enumerable.Range(0, 100))
        {
            _b.Folder.WriteFile($"d{i % 50:D2}/f{i:D4}.txt", $"edited {i}");
        }

        Ok(await _b.SyncAsync()).Uploaded.Files.ShouldBe(100);
        SyncReport pulled = Ok(await _a.SyncAsync());

        pulled.Downloaded.Files.ShouldBe(100);
        _a.Folder.Files().ShouldBe(_b.Folder.Files());
        Ok(await _a.SyncAsync()).Downloaded.Files.ShouldBe(0); // and a no-change sync reads nothing new
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private static void SkipUnlessEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1", $"Large-share tests run only with {Variable}=1 (the real-Azure workflow).");

    // Random bytes, streamed: a 300 MB array would be a test of the test machine's memory.
    private static async Task<string> WriteRandomAsync(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        await using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
        for (long left = size; left > 0;)
        {
            int count = (int)Math.Min(buffer.Length, left);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
            sha256.AppendData(buffer, 0, count);
            await file.WriteAsync(buffer.AsMemory(0, count), Ct);
            left -= count;
        }

        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    private static async Task<string> HashAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, Ct));
    }
}
