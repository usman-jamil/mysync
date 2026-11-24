using System.Text.Json;
using MySync.Domain.State;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>
/// The built <c>mysync</c> with <c>MYSYNC_PROVIDER=s3</c>: the provider deciding which variables are
/// read (the other provider's are ignored), folders of one provider refused under the other, and a
/// round trip against the real test bucket (CLAUDE.md §5, §10). Credentials reach the process the
/// standard AWS way.
/// </summary>
public sealed class S3CliTests(StorageFixture storage) : IDisposable
{
    private const string OfflineAzure = "DefaultEndpointsProtocol=https;AccountName=teamsyncstorage;AccountKey=c2VjcmV0LWtleS1mb3ItdGVzdHM=;EndpointSuffix=core.windows.net";

    private readonly TempShareFolder _a = new();
    private readonly TempShareFolder _b = new();

    // `status` outside a share passes the storage settings check, then stops with "not a share"
    // (exit 4) before any request: so exit 4 means the settings were accepted.

    [Fact]
    public async Task S3_Should_IgnoreAnAzureConnectionString()
    {
        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>
        {
            ["MYSYNC_PROVIDER"] = "s3",
            ["MYSYNC_S3_BUCKET"] = "team-shares",
            ["MYSYNC_S3_REGION"] = "eu-west-1",
            ["AZURE_STORAGE_CONNECTION_STRING"] = "not a connection string at all",
        }, "status", "--json");

        result.ExitCode.ShouldBe(4, result.ToString());
        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("Shares.NotAShare");
    }

    [Fact]
    public async Task AzureByDefault_Should_IgnoreTheS3Settings()
    {
        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_CONNECTION_STRING"] = OfflineAzure,
            ["MYSYNC_S3_BUCKET"] = "Not A Valid Bucket",
            ["MYSYNC_S3_REGION"] = "nowhere",
        }, "status", "--json");

        result.ExitCode.ShouldBe(4, result.ToString());
        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("Shares.NotAShare");
    }

    [Fact]
    public async Task S3_Should_RequireOnlyItsOwnSettings_NotAzures()
    {
        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>
        {
            ["MYSYNC_PROVIDER"] = "s3",
            ["MYSYNC_S3_BUCKET"] = "team-shares",
            ["MYSYNC_S3_REGION"] = "eu-west-1",
        }, "status");

        result.ExitCode.ShouldBe(4, result.ToString());
        result.StdErr.ShouldNotContain("AZURE_STORAGE_CONNECTION_STRING");
    }

    [Fact]
    public async Task AnUnknownProvider_Should_NameBoth()
    {
        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_PROVIDER"] = "gcs" }, "status");

        result.ExitCode.ShouldBe(2, result.ToString());
        result.StdErr.ShouldContain("Supported: azure-blob, s3");
    }

    [Fact]
    public async Task AMissingBucketOrRegion_Should_ExitTwo_NamingTheVariable()
    {
        CliResult noBucket = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_PROVIDER"] = "s3" }, "status");
        CliResult noRegion = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["MYSYNC_PROVIDER"] = "s3", ["MYSYNC_S3_BUCKET"] = "team-shares", ["AWS_REGION"] = null }, "status");

        noBucket.ExitCode.ShouldBe(2, noBucket.ToString());
        noBucket.StdErr.ShouldContain("MYSYNC_S3_BUCKET");
        noRegion.ExitCode.ShouldBe(2, noRegion.ToString());
        noRegion.StdErr.ShouldContain("MYSYNC_S3_REGION");
    }

    [Fact]
    public async Task AFolderCreatedOnAzure_Should_BeRefusedUnderS3_AndChangeNothing()
    {
        WriteState(_a, new StorageLocation("azure-blob", "teamsyncstorage", "mysync"));
        _a.WriteFile("local.txt", "mine");
        string before = _a.ReadText(StateFileNames.State);

        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?>
        {
            ["MYSYNC_PROVIDER"] = "s3",
            ["MYSYNC_S3_BUCKET"] = "team-shares",
            ["MYSYNC_S3_REGION"] = "eu-west-1",
        }, "sync", "--json");

        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("State.StorageMismatch");
        result.StdErr.ShouldContain("This folder belongs to azure-blob (account teamsyncstorage, container mysync), but the environment points at s3 (bucket team-shares in eu-west-1)");
        result.StdErr.ShouldContain("Set MYSYNC_PROVIDER=azure-blob");
        _a.ReadText(StateFileNames.State).ShouldBe(before);
        _a.ReadText("local.txt").ShouldBe("mine");
    }

    [Fact]
    public async Task AFolderCreatedOnS3_Should_BeRefusedUnderAzure_AndChangeNothing()
    {
        WriteState(_a, new StorageLocation("s3", "eu-west-1", "team-shares"));
        string before = _a.ReadText(StateFileNames.State);

        CliResult result = await CliProcess.RunAsync(_a.Path, new Dictionary<string, string?> { ["AZURE_STORAGE_CONNECTION_STRING"] = OfflineAzure }, "status", "--json");

        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("State.StorageMismatch");
        result.StdErr.ShouldContain("Set MYSYNC_PROVIDER=s3 and its settings (MYSYNC_S3_BUCKET, MYSYNC_S3_REGION)");
        _a.ReadText(StateFileNames.State).ShouldBe(before);
    }

    [Fact]
    public async Task CreateCloneSyncInfoAndDelete_Should_WorkOnS3()
    {
        Dictionary<string, string?> environment = S3Environment();
        string name = "s3cli-" + Guid.NewGuid().ToString("N")[..12];
        _a.WriteFile("docs/readme.md", "# hi\n").WriteFile("run.sh", "#!/bin/sh\n", executable: true);

        CliResult created = await CliProcess.RunAsync(_a.Path, environment, "sync", "--name", name, "--json");
        created.ExitCode.ShouldBe(0, created.ToString());
        using var createdJson = JsonDocument.Parse(created.StdOut);
        string shareId = createdJson.RootElement.GetProperty("shareId").GetString()!;
        try
        {
            CliResult cloned = await CliProcess.RunAsync(_b.Path, environment, "clone", name, "copy");
            cloned.ExitCode.ShouldBe(0, cloned.ToString());
            string copy = Path.Combine(_b.Path, "copy");
            (await File.ReadAllTextAsync(Path.Combine(copy, "docs", "readme.md"), TestContext.Current.CancellationToken)).ShouldBe("# hi\n");

            await File.WriteAllTextAsync(Path.Combine(copy, "from-b.txt"), "b\n", TestContext.Current.CancellationToken);
            (await CliProcess.RunAsync(copy, environment, "sync")).ExitCode.ShouldBe(0);
            (await CliProcess.RunAsync(_a.Path, environment, "sync")).ExitCode.ShouldBe(0);
            _a.ReadText("from-b.txt").ShouldBe("b\n");

            CliResult info = await CliProcess.RunAsync(_a.Path, environment, "info", "--json");
            info.ExitCode.ShouldBe(0, info.ToString());
            using var infoJson = JsonDocument.Parse(info.StdOut);
            infoJson.RootElement.GetProperty("provider").GetString().ShouldBe("s3");
            infoJson.RootElement.GetProperty("accountName").GetString().ShouldBe(storage.S3Storage.Region);
            infoJson.RootElement.GetProperty("container").GetString().ShouldBe(storage.S3Storage.Bucket);
            (await CliProcess.RunAsync(_a.Path, environment, "info")).StdOut.ShouldContain($"s3 (bucket {storage.S3Storage.Bucket} in {storage.S3Storage.Region})");

            CliResult shares = await CliProcess.RunAsync(_a.Path, environment, "shares", "--json");
            shares.ExitCode.ShouldBe(0, shares.ToString());
            shares.StdOut.ShouldContain(name);

            CliResult deleted = await CliProcess.RunAsync(_a.Path, environment, "delete", "--yes");
            deleted.ExitCode.ShouldBe(0, deleted.ToString());
            (await CliProcess.RunAsync(copy, environment, "sync")).ExitCode.ShouldBe(8);
            (await CliProcess.RunAsync(_b.Path, environment, "clone", name, "again")).ExitCode.ShouldBe(8, "the name is free again");
        }
        finally
        {
            // This run's share and name sit at the bucket's root (no test prefix through the CLI): remove them.
            await storage.S3Storage.DeleteAsync(storage.S3Storage.Bucket, [.. await storage.S3Storage.ListAsync($"shares/{shareId}/"), .. await storage.S3Storage.ListAsync($"aliases/{name}")]);
        }
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private Dictionary<string, string?> S3Environment() => new()
    {
        ["MYSYNC_PROVIDER"] = "s3",
        ["MYSYNC_S3_BUCKET"] = storage.S3Storage.Bucket, // skips (or fails in CI) without a test bucket
        ["MYSYNC_S3_REGION"] = storage.S3Storage.Region,
    };

    private static void WriteState(TempShareFolder folder, StorageLocation location) =>
        folder.WriteFile(StateFileNames.State, $$"""
            {
              "formatVersion": 1,
              "provider": "{{location.Provider}}",
              "accountName": "{{location.AccountName}}",
              "container": "{{location.Container}}",
              "shareId": "0b6f3c1e-8f0a-4c43-9d3e-5a1f2b7c9e10",
              "baseRevision": 1,
              "base": {},
              "hashCache": {},
              "conflicts": []
            }
            """);
}
