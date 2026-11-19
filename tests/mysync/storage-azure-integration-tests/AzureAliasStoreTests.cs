using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Testing;
using MySync.Testing.Contracts;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.Azure.IntegrationTests;

/// <summary>The alias contract on Azurite: each store gets its own container.</summary>
public sealed class AzureAliasStoreContractTests(AzuriteFixture azurite) : AliasStoreContractTests
{
    protected override async Task<IAliasStore> CreateStoreAsync(int? pageSize = null)
    {
        string container = AzuriteEnvironment.NewContainerName();
        await new BlobContainerClient(azurite.ConnectionString, container).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        return AzureAliases.Create(azurite.ConnectionString, container, pageSize);
    }
}

/// <summary>What only the Azure alias store has: the blob's shape, damaged or stray blobs, configuration.</summary>
public sealed class AzureAliasStoreTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(AzureAliasStore Store, BlobContainerClient Container)> StoreAsync()
    {
        string name = AzuriteEnvironment.NewContainerName();
        BlobContainerClient container = new(azurite.ConnectionString, name);
        await container.CreateAsync(cancellationToken: Ct);
        return (AzureAliases.Create(azurite.ConnectionString, name, pageSize: null), container);
    }

    [Fact]
    public async Task AnAlias_Should_BeAJsonBlob_WithTheSameValuesInMetadata()
    {
        (AzureAliasStore store, BlobContainerClient container) = await StoreAsync();
        var shareId = ShareId.New();
        AliasRecord alias = new(ShareName.Create("next-wave").Value, shareId, new DateTimeOffset(2026, 11, 19, 8, 30, 0, TimeSpan.Zero), "Üser@HOST");

        (await store.CreateAsync(alias, Ct)).IsSuccess.ShouldBeTrue();

        BlobClient blob = container.GetBlobClient("aliases/next-wave");
        BlobProperties properties = (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentType.ShouldBe("application/json");
        properties.Metadata["mysyncshareid"].ShouldBe(shareId.ToString());
        properties.Metadata["mysynccreatedutc"].ShouldBe("2026-11-19T08:30:00Z");
        properties.Metadata["mysynccreatedby"].ShouldBe("%C3%9Cser%40HOST");
        using var body = JsonDocument.Parse((await blob.DownloadContentAsync(Ct)).Value.Content.ToString());
        body.RootElement.GetProperty("shareId").GetString().ShouldBe(shareId.ToString());
        body.RootElement.GetProperty("createdBy").GetString().ShouldBe("Üser@HOST");
        body.RootElement.TryGetProperty("createdUtc", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ADamagedOrStrayBlob_Should_BeReported_WithoutStoppingTheListing()
    {
        (AzureAliasStore store, BlobContainerClient container) = await StoreAsync();
        AliasRecord good = new(ShareName.Create("good-name").Value, ShareId.New(), DateTimeOffset.UnixEpoch, "me");
        (await store.CreateAsync(good, Ct)).IsSuccess.ShouldBeTrue();
        await container.GetBlobClient("aliases/broken-name").UploadAsync(BinaryData.FromString("{}"), Ct);
        await container.GetBlobClient("aliases/Not_A.Name").UploadAsync(BinaryData.FromString("{}"), Ct);

        List<Result<AliasRecord>> listed = [];
        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            listed.Add(item);
        }

        listed.Single(item => item.IsSuccess).Value.ShouldBe(good with { CreatedUtc = DateTimeOffset.UnixEpoch });
        Error broken = listed.Single(item => item.IsFailure && item.Error.Code == "Storage.InvalidAlias").Error;
        broken.Description.ShouldContain("broken-name");
        listed.Single(item => item.IsFailure && item.Error.Code == "Storage.StrayAlias").Error.Description.ShouldContain("aliases/Not_A.Name");
        (await store.GetAsync(ShareName.Create("broken-name").Value, Ct)).Error.Code.ShouldBe("Storage.InvalidAlias");
        (await store.FindByShareAsync(good.ShareId, Ct)).Value.Single().Name.ShouldBe(good.Name);
    }

    [Fact]
    public async Task AContainerThatDoesNotExist_Should_HaveNoNames()
    {
        AzureAliasStore store = AzureAliases.Create(azurite.ConnectionString, AzuriteEnvironment.NewContainerName(), pageSize: null);

        (await store.GetAsync(ShareName.Create("next-wave").Value, Ct)).Value.ShouldBeNull();
        (await store.FindByShareAsync(ShareId.New(), Ct)).Value.ShouldBeEmpty();
        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            item.ShouldBeNull("there's no container");
        }
    }

    [Fact]
    public async Task MissingOrMalformedSettings_Should_BeConfigurationErrors_WithoutTheKey()
    {
        const string Key = "c2VjcmV0LWtleS0xMjM=";
        AzureAliasStore missing = AzureAliases.Create(null, "mysync", pageSize: null);
        AzureAliasStore malformed = AzureAliases.Create($"AccountName=x;AccountKey={Key};Garbage", "mysync", pageSize: null);

        (await missing.GetAsync(ShareName.Create("next-wave").Value, Ct)).Error.Code.ShouldBe("Configuration.Missing");
        Error error = (await malformed.CreateAsync(new AliasRecord(ShareName.Create("next-wave").Value, ShareId.New(), DateTimeOffset.UnixEpoch, "me"), Ct)).Error;
        error.Code.ShouldBe("Configuration.Invalid");
        error.Description.ShouldNotContain(Key);
    }
}

internal static class AzureAliases
{
    public static AzureAliasStore Create(string? connectionString, string container, int? pageSize) =>
        new(Options.Create(new AzureStorageOptions { ConnectionString = connectionString, Container = container }), AzureStores.TestSettings, pageSize, logger: null);
}
