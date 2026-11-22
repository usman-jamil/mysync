using MySync.Application.Abstractions.Storage;
using MySync.Application.Shares.Create;
using MySync.Application.Shares.Delete;
using MySync.Application.Shares.GetInfo;
using MySync.Application.Shares.List;
using MySync.Application.Shares.Rename;
using MySync.Application.Sync.Run;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Shares;

/// <summary>Share names: claimed on create, resolved by clone and the rest, moved by rename, freed by delete, listed by shares.</summary>
public class ShareNameUseCaseTests
{
    private readonly SyncWorld _world = new();

    private static ShareName Name(string value) => ShareName.Create(value).Value;

    private async Task<Machine> CreatedAsync(string name, string machine = "usman@DEV-LAPTOP")
    {
        Machine m = _world.Machine(machine);
        m.Folder.WriteFile("docs/readme.md", "# hi").WriteFile("notes.txt", "n");
        (await m.CreateAsync(new CreateShareCommand(Name(name)))).ShouldSucceed();
        return m;
    }

    private async Task<AliasRecord?> AliasAsync(string name) => (await _world.Aliases.GetAsync(Name(name), SyncWorld.Ct)).Value?.Alias;

    // ---------- create ----------

    [Fact]
    public async Task Create_WithoutAName_Should_FailBeforeWritingAnything_AndSuggestOne()
    {
        Machine machine = _world.Machine("me", new InMemoryLocalFolder { RootPath = "/home/me/Q3 Reports" });
        machine.Folder.WriteFile("a.txt", "a");

        Error error = (await machine.CreateExactlyAsync(new CreateShareCommand())).ShouldFail();

        error.Code.ShouldBe("Shares.NameRequired");
        error.Description.ShouldBe("A new share needs a name: mysync sync --name q3-reports");
        _world.Store.BlobNames.ShouldBeEmpty();
        _world.Aliases.BlobNames.ShouldBeEmpty();
        machine.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task Create_WithATakenName_Should_FailBeforeUploading_AndNameTheShareThatHasIt()
    {
        Machine first = await CreatedAsync("next-wave");
        IReadOnlyList<string> before = _world.Store.BlobNames;
        Machine second = _world.Machine("other@SERVER01");
        second.Folder.WriteFile("big.bin", "data");

        Error error = (await second.CreateAsync(new CreateShareCommand(Name("next-wave")))).ShouldFail();

        error.Code.ShouldBe("Shares.NameTaken");
        error.Description.ShouldContain(first.ShareId.ToString());
        _world.Store.BlobNames.ShouldBe(before);
        second.State.State.ShouldBeNull();
    }

    [Fact]
    public async Task Create_Should_ClaimTheName_BeforeUploadingOrCommitting()
    {
        Machine machine = _world.Machine("me");
        machine.Folder.WriteFile("a.txt", "a");
        List<int> blobsWhenClaimed = [];
        _world.Aliases.BeforeCreate = _ =>
        {
            blobsWhenClaimed.Add(_world.Store.BlobNames.Count);
            return Task.CompletedTask;
        };

        CreateShareResponse created = (await machine.CreateAsync(new CreateShareCommand(Name("next-wave")))).ShouldSucceed();

        blobsWhenClaimed.ShouldBe([0]); // no object, no manifest yet
        created.Name.ShouldBe(Name("next-wave"));
        (await AliasAsync("next-wave"))!.ShareId.ShouldBe(created.ShareId);
        (await AliasAsync("next-wave"))!.CreatedBy.ShouldBe("me");
        (await _world.HeadAsync(created.ShareId)).Revision.Value.ShouldBe(1);
    }

    [Fact]
    public async Task TwoCreatesRacingForOneName_Should_MakeOneShare_AndOneNameTaken()
    {
        Machine a = _world.Machine("a");
        Machine b = _world.Machine("b");
        a.Folder.WriteFile("a.txt", "a");
        b.Folder.WriteFile("b.txt", "b");

        Result<CreateShareResponse>[] results = await Task.WhenAll(
            a.CreateAsync(new CreateShareCommand(Name("contested"))),
            b.CreateAsync(new CreateShareCommand(Name("contested"))));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Single(result => result.IsFailure).Error.Code.ShouldBe("Shares.NameTaken");
        _world.Store.BlobNames.Count(name => name.EndsWith("/manifest.json", StringComparison.Ordinal)).ShouldBe(1);
    }

    // ---------- clone ----------

    [Fact]
    public async Task Clone_Should_WorkByNameOrById()
    {
        Machine first = await CreatedAsync("next-wave");

        Machine byName = _world.Machine("b");
        SyncReport report = (await byName.CloneAsync("next-wave")).ShouldSucceed();
        report.Name.ShouldBe(Name("next-wave"));
        byName.Files()["docs/readme.md"].ShouldBe("# hi");

        Machine byId = _world.Machine("c");
        (await byId.CloneAsync(first.ShareId)).ShouldSucceed().Name.ShouldBeNull();
        byId.ShareId.ShouldBe(first.ShareId);
    }

    [Fact]
    public async Task Clone_Should_SayWhenNoShareHasTheName()
    {
        (await _world.Machine("b").CloneAsync("no-such-share")).ShouldFail().ShouldBe(ShareErrors.NameNotFound(Name("no-such-share")));
    }

    [Fact]
    public async Task Clone_Should_SayWhenTheNamedShareWasNeverFinished()
    {
        var unfinished = ShareId.New();
        (await _world.Aliases.CreateAsync(new AliasRecord(Name("half-made"), unfinished, DateTimeOffset.UnixEpoch, "x"), SyncWorld.Ct)).ShouldSucceed();

        (await _world.Machine("b").CloneAsync("half-made")).ShouldFail().ShouldBe(ShareErrors.Incomplete(Name("half-made"), unfinished));
    }

    [Fact]
    public async Task Clone_Should_SayWhenTheNamedShareWasDeleted()
    {
        Machine first = await CreatedAsync("next-wave");
        ShareId shareId = first.ShareId;
        (await first.DeleteAsync()).ShouldSucceed();
        // A name left behind (e.g. an older client's delete) still points at the deleted share.
        (await _world.Aliases.CreateAsync(new AliasRecord(Name("next-wave"), shareId, DateTimeOffset.UnixEpoch, "x"), SyncWorld.Ct)).ShouldSucceed();

        (await _world.Machine("b").CloneAsync("next-wave")).ShouldFail().Code.ShouldBe("Shares.Deleted");
    }

    // ---------- rename ----------

    [Fact]
    public async Task Rename_Should_ClaimTheNewName_ThenFreeTheOld()
    {
        Machine machine = await CreatedAsync("old-name");

        RenameShareResponse renamed = (await machine.RenameAsync("new-name")).ShouldSucceed();

        renamed.Previous.ShouldBe([Name("old-name")]);
        renamed.Unchanged.ShouldBeFalse();
        (await AliasAsync("new-name"))!.ShareId.ShouldBe(machine.ShareId);
        (await AliasAsync("old-name")).ShouldBeNull();
        _world.Aliases.Operations.TakeLast(2).ShouldBe(["create:new-name", "delete:old-name"]);
    }

    [Fact]
    public async Task Rename_ToATakenName_Should_FailAndKeepTheCurrentName()
    {
        Machine mine = await CreatedAsync("mine");
        Machine theirs = await CreatedAsync("theirs", "other");

        (await mine.RenameAsync("theirs")).ShouldFail().ShouldBe(ShareErrors.NameTaken(Name("theirs"), theirs.ShareId));

        (await AliasAsync("mine"))!.ShareId.ShouldBe(mine.ShareId);
        (await AliasAsync("theirs"))!.ShareId.ShouldBe(theirs.ShareId);
    }

    [Fact]
    public async Task Rename_ToTheSameName_Should_ChangeNothing()
    {
        Machine machine = await CreatedAsync("same-name");
        int operations = _world.Aliases.Operations.Count;

        RenameShareResponse renamed = (await machine.RenameAsync("same-name")).ShouldSucceed();

        renamed.Unchanged.ShouldBeTrue();
        _world.Aliases.Operations.Count.ShouldBe(operations);
    }

    [Fact]
    public async Task Rename_Should_NameAShareThatHadNoName()
    {
        Machine machine = await CreatedAsync("temporary");
        AliasSnapshot alias = (await _world.Aliases.GetAsync(Name("temporary"), SyncWorld.Ct)).Value!;
        (await _world.Aliases.DeleteAsync(alias.Alias.Name, alias.ETag, SyncWorld.Ct)).IsSuccess.ShouldBeTrue(); // as if made before names

        RenameShareResponse renamed = (await machine.RenameAsync("first-name")).ShouldSucceed();

        renamed.Previous.ShouldBeEmpty();
        (await AliasAsync("first-name"))!.ShareId.ShouldBe(machine.ShareId);
    }

    [Fact]
    public async Task ARenameInterruptedAfterTheClaim_Should_LeaveTwoWorkingNames_ThatTheNextRenameCleansUp()
    {
        Machine machine = await CreatedAsync("old-name");
        _world.Aliases.BeforeDelete = _ => throw new IOException("the network dropped");

        await Should.ThrowAsync<IOException>(() => machine.RenameAsync("new-name"));
        _world.Aliases.BeforeDelete = null;

        (await _world.Aliases.FindByShareAsync(machine.ShareId, SyncWorld.Ct)).Value.Count.ShouldBe(2);
        (await _world.Machine("b").InfoAsync("old-name")).ShouldSucceed().ShareId.ShouldBe(machine.ShareId);
        (await _world.Machine("c").InfoAsync("new-name")).ShouldSucceed().Names.ShouldBe([Name("new-name"), Name("old-name")]);

        RenameShareResponse again = (await machine.RenameAsync("new-name")).ShouldSucceed();

        again.Previous.ShouldBe([Name("old-name")]);
        again.Unchanged.ShouldBeFalse();
        (await _world.Aliases.FindByShareAsync(machine.ShareId, SyncWorld.Ct)).Value.Single().Name.ShouldBe(Name("new-name"));
    }

    // ---------- delete ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_Should_FreeTheName_WhetherByNameOrById(bool byName)
    {
        Machine machine = await CreatedAsync("next-wave");
        ShareId shareId = machine.ShareId;

        DeleteShareResponse deleted = (await _world.Machine("admin").DeleteAsync(byName ? "next-wave" : shareId.ToString())).ShouldSucceed();

        deleted.ShareId.ShouldBe(shareId);
        deleted.NamesFreed.ShouldBe([Name("next-wave")]);
        (await AliasAsync("next-wave")).ShouldBeNull();
        (await CreatedAsync("next-wave", "someone-new")).ShareId.ShouldNotBe(shareId); // free to reuse
    }

    [Fact]
    public async Task Delete_Should_FreeEveryNameOfTheShare()
    {
        Machine machine = await CreatedAsync("first-name");
        (await _world.Aliases.CreateAsync(new AliasRecord(Name("second-name"), machine.ShareId, DateTimeOffset.UnixEpoch, "x"), SyncWorld.Ct)).ShouldSucceed();

        DeleteShareResponse deleted = (await machine.DeleteAsync()).ShouldSucceed();

        deleted.NamesFreed.Select(name => name.Value).Order(StringComparer.Ordinal).ShouldBe(["first-name", "second-name"]);
        _world.Aliases.BlobNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInterruptedDelete_Should_FreeTheNameWhenRunAgain()
    {
        Machine machine = await CreatedAsync("next-wave");
        ShareId shareId = machine.ShareId;
        _world.Aliases.BeforeDelete = _ => throw new IOException("interrupted");
        await Should.ThrowAsync<IOException>(() => _world.Machine("admin").DeleteAsync(shareId.ToString()));
        _world.Aliases.BeforeDelete = null;
        (await _world.HeadAsync(shareId)).IsDeleted.ShouldBeTrue();
        (await AliasAsync("next-wave")).ShouldNotBeNull();

        DeleteShareResponse again = (await _world.Machine("admin").DeleteAsync("next-wave")).ShouldSucceed();

        again.WasAlreadyDeleted.ShouldBeTrue();
        again.NamesFreed.ShouldBe([Name("next-wave")]);
        (await AliasAsync("next-wave")).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Should_NotRemoveANameThatWasRepointedMeanwhile()
    {
        Machine machine = await CreatedAsync("next-wave");
        var someoneElse = ShareId.New();
        _world.Aliases.BeforeDelete = name => _world.Aliases.Repoint(name, someoneElse);

        DeleteShareResponse deleted = (await machine.DeleteAsync()).ShouldSucceed();

        deleted.NamesFreed.ShouldBeEmpty();
        (await AliasAsync("next-wave"))!.ShareId.ShouldBe(someoneElse);
    }

    [Fact]
    public async Task Delete_Should_NotRemoveANameRepointedBetweenFindingAndRereadingIt()
    {
        Machine machine = await CreatedAsync("next-wave");
        var someoneElse = ShareId.New();
        _world.Aliases.AfterFindByShare = found =>
        {
            foreach (AliasRecord alias in found)
            {
                _world.Aliases.Repoint(alias.Name, someoneElse);
            }
        };

        DeleteShareResponse deleted = (await machine.DeleteAsync()).ShouldSucceed();

        deleted.NamesFreed.ShouldBeEmpty();
        (await AliasAsync("next-wave"))!.ShareId.ShouldBe(someoneElse);
    }

    [Fact]
    public async Task Delete_Should_RemoveAShareThatWasNeverFinished_ByItsName()
    {
        var unfinished = ShareId.New();
        (await _world.Aliases.CreateAsync(new AliasRecord(Name("half-made"), unfinished, DateTimeOffset.UnixEpoch, "x"), SyncWorld.Ct)).ShouldSucceed();
        byte[] content = "uploaded before the create was interrupted"u8.ToArray();
        (await _world.Store.UploadObjectAsync(unfinished, ManifestBuilder.HashOf(content), ObjectUpload.Raw(new MemoryStream(content), content.Length), SyncWorld.Ct)).IsSuccess.ShouldBeTrue();

        DeleteShareResponse deleted = (await _world.Machine("admin").DeleteAsync("half-made")).ShouldSucceed();

        deleted.WasIncomplete.ShouldBeTrue();
        deleted.MarkerRevision.ShouldBeNull();
        deleted.ObjectsDeleted.ShouldBe(1);
        deleted.NamesFreed.ShouldBe([Name("half-made")]);
        _world.Store.BlobNames.ShouldNotContain(name => name.StartsWith(ShareLayout.For(unfinished).Prefix, StringComparison.Ordinal));
        _world.Aliases.BlobNames.ShouldBeEmpty();
    }

    // ---------- list, info, publish ----------

    [Fact]
    public async Task Shares_Should_ListEveryName_Sorted_AndReportUnreadableOnes()
    {
        Machine zeta = await CreatedAsync("zeta");
        Machine alpha = await CreatedAsync("alpha", "other");
        _world.Aliases.PutMalformed("broken-name");
        _world.Aliases.PutStray("aliases/Not_A.Name");

        ShareListResponse listed = (await _world.Machine("viewer").SharesAsync()).ShouldSucceed();

        listed.Shares.Select(alias => (alias.Name.Value, alias.ShareId)).ShouldBe([("alpha", alpha.ShareId), ("zeta", zeta.ShareId)]);
        listed.Warnings.Count.ShouldBe(2);
        listed.Warnings.ShouldContain(warning => warning.Contains("broken-name", StringComparison.Ordinal));
        listed.Warnings.ShouldContain(warning => warning.Contains("aliases/Not_A.Name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Info_Should_ShowTheNames_HereOrFromAnywhere()
    {
        Machine machine = await CreatedAsync("next-wave");

        ShareInfoResponse here = (await machine.InfoAsync()).ShouldSucceed();
        here.Names.ShouldBe([Name("next-wave")]);
        here.BaseRevision.ShouldNotBeNull();

        ShareInfoResponse elsewhere = (await _world.Machine("viewer").InfoAsync("next-wave")).ShouldSucceed();
        elsewhere.ShareId.ShouldBe(machine.ShareId);
        elsewhere.BaseRevision.ShouldBeNull();
        elsewhere.OpenConflicts.ShouldBeNull();
        elsewhere.RootPath.ShouldBeNull();
    }

    [Fact]
    public async Task Publish_Should_AcceptTheShareByName()
    {
        Machine machine = await CreatedAsync("next-wave");

        (await _world.Machine("other").PublishAsync(new MySync.Application.Publishing.Publish.PublishShareCommand(Share: ShareReference.Parse("next-wave").Value)))
            .ShouldSucceed().ShareId.ShouldBe(machine.ShareId);
    }
}
