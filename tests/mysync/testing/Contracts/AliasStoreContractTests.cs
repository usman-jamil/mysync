using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Testing.Contracts;

/// <summary>
/// The behaviour every <see cref="IAliasStore"/> must have. Derive once per implementation:
/// <see cref="InMemoryAliasStore"/> in the application unit tests, Azure and S3 against real test
/// storage in their integration tests. If the fake and a real provider disagree, the fake is wrong.
/// </summary>
public abstract class AliasStoreContractTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 19, 8, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh, isolated, empty store whose container exists.</summary>
    /// <param name="pageSize">Aliases per listing page, to make a listing cross pages; <c>null</c> for the default.</param>
    protected abstract Task<IAliasStore> CreateStoreAsync(int? pageSize = null);

    protected static ShareName Name(string value) => ShareName.Create(value).Value;

    protected static AliasRecord Alias(string name, ShareId? shareId = null, string createdBy = "usman@DEV-LAPTOP") =>
        new(Name(name), shareId ?? ShareId.New(), Created, createdBy);

    [Fact]
    public async Task Create_Should_StoreTheAlias_AndGetShouldReadItBack_WithTheSameETag()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord alias = Alias("next-wave");

        string etag = Unwrap(await store.CreateAsync(alias, Ct));

        AliasSnapshot read = Unwrap(await store.GetAsync(alias.Name, Ct))!;
        read.Alias.ShouldBe(alias);
        read.ETag.ShouldBe(etag);
    }

    [Fact]
    public async Task ASecondCreate_Should_FailWithAlreadyExists_AndLeaveTheOriginalAlone()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord original = Alias("next-wave");
        Unwrap(await store.CreateAsync(original, Ct));

        Error error = (await store.CreateAsync(Alias("next-wave"), Ct)).Error;

        error.Code.ShouldBe("Storage.AlreadyExists");
        Unwrap(await store.GetAsync(original.Name, Ct))!.Alias.ShareId.ShouldBe(original.ShareId);
    }

    [Fact]
    public async Task CreatesRacingForOneName_Should_LetExactlyOneWin()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord[] contenders = [.. Enumerable.Range(0, 8).Select(_ => Alias("contested"))];

        Result<string>[] results = await Task.WhenAll(contenders.Select(alias => store.CreateAsync(alias, Ct)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailure).ShouldAllBe(result => result.Error.Code == "Storage.AlreadyExists");
        AliasRecord winner = contenders[Array.FindIndex(results, result => result.IsSuccess)];
        Unwrap(await store.GetAsync(Name("contested"), Ct))!.Alias.ShareId.ShouldBe(winner.ShareId);
    }

    [Fact]
    public async Task Get_Should_ReturnNull_ForAMissingName()
    {
        IAliasStore store = await CreateStoreAsync();

        (await store.GetAsync(Name("no-such-name"), Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task List_Should_ReturnEveryAlias_AcrossPages()
    {
        IAliasStore store = await CreateStoreAsync(pageSize: 2);
        AliasRecord[] aliases = [.. Enumerable.Range(1, 5).Select(i => Alias($"share-{i:D2}"))];
        foreach (AliasRecord alias in aliases)
        {
            Unwrap(await store.CreateAsync(alias, Ct));
        }

        List<AliasRecord> listed = [];
        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            listed.Add(Unwrap(item));
        }

        listed.OrderBy(alias => alias.Name.Value, StringComparer.Ordinal).ShouldBe(aliases);
    }

    [Fact]
    public async Task List_Should_BeEmpty_WithNoAliases()
    {
        IAliasStore store = await CreateStoreAsync();

        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            item.ShouldBeNull("there are no aliases");
        }
    }

    [Fact]
    public async Task FindByShare_Should_ReturnEveryAliasOfThatShare_AndNoOthers()
    {
        IAliasStore store = await CreateStoreAsync();
        var renamed = ShareId.New();
        var other = ShareId.New();
        Unwrap(await store.CreateAsync(Alias("old-name", renamed), Ct));
        Unwrap(await store.CreateAsync(Alias("new-name", renamed), Ct)); // an interrupted rename
        Unwrap(await store.CreateAsync(Alias("someone-else", other), Ct));

        IReadOnlyList<AliasRecord> found = Unwrap(await store.FindByShareAsync(renamed, Ct));

        found.Select(alias => alias.Name.Value).Order(StringComparer.Ordinal).ShouldBe(["new-name", "old-name"]);
        Unwrap(await store.FindByShareAsync(other, Ct)).Single().Name.Value.ShouldBe("someone-else");
        Unwrap(await store.FindByShareAsync(ShareId.New(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_Should_RefuseAStaleETag_AndKeepTheAlias()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord first = Alias("next-wave");
        string stale = Unwrap(await store.CreateAsync(first, Ct));
        Unwrap(await store.DeleteAsync(first.Name, stale, Ct));
        AliasRecord second = Alias("next-wave"); // the name now points at another share
        Unwrap(await store.CreateAsync(second, Ct));

        (await store.DeleteAsync(first.Name, stale, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");

        Unwrap(await store.GetAsync(first.Name, Ct))!.Alias.ShareId.ShouldBe(second.ShareId);
    }

    /// <remarks>
    /// S3's ETag follows an object's bytes, not the write. The alias body holds the share ID, so a
    /// name re-pointed at another share has another ETag, and a conditional delete that read the old
    /// one can never remove the new alias.
    /// </remarks>
    [Fact]
    public async Task AnAliasRecreated_ForAnotherShare_Should_HaveAnotherETag_AndTheOldOneNotDeleteIt()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord first = Alias("re-pointed");
        string old = Unwrap(await store.CreateAsync(first, Ct));
        Unwrap(await store.DeleteAsync(first.Name, old, Ct));
        AliasRecord second = first with { ShareId = ShareId.New() }; // same name, time and author: only the share differs

        string recreated = Unwrap(await store.CreateAsync(second, Ct));

        recreated.ShouldNotBe(old);
        (await store.DeleteAsync(first.Name, old, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
        AliasSnapshot left = Unwrap(await store.GetAsync(first.Name, Ct))!;
        left.Alias.ShareId.ShouldBe(second.ShareId);
        left.ETag.ShouldBe(recreated);
    }

    [Fact]
    public async Task Delete_Should_RemoveTheAlias_WithTheCurrentETag_AndFailWhenItIsGone()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord alias = Alias("next-wave");
        Unwrap(await store.CreateAsync(alias, Ct));
        string current = Unwrap(await store.GetAsync(alias.Name, Ct))!.ETag;

        Unwrap(await store.DeleteAsync(alias.Name, current, Ct));

        (await store.GetAsync(alias.Name, Ct)).Value.ShouldBeNull();
        (await store.DeleteAsync(alias.Name, current, Ct)).Error.Code.ShouldBe("Storage.PreconditionFailed");
        Unwrap(await store.CreateAsync(Alias("next-wave"), Ct)); // a freed name can be claimed again
    }

    [Fact]
    public async Task Metadata_Should_RoundTrip_IncludingANonAsciiCreatedBy()
    {
        IAliasStore store = await CreateStoreAsync();
        AliasRecord alias = Alias("unicode-owner", createdBy: "Ünïcode Üser@日本-ラップトップ;=%20");
        Unwrap(await store.CreateAsync(alias, Ct));

        Unwrap(await store.GetAsync(alias.Name, Ct))!.Alias.ShouldBe(alias);
        await foreach (Result<AliasRecord> item in store.ListAsync(Ct))
        {
            Unwrap(item).ShouldBe(alias);
        }
    }

    protected static T Unwrap<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
        return result.Value;
    }

    protected static void Unwrap(Result result) =>
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Description}" : null);
}
