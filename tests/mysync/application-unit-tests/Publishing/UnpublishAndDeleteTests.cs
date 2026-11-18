using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;
using MySync.Application.Shares.Delete;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Configuration;
using MySync.Domain.Publishing;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Publishing;

/// <summary><c>unpublish</c>, <c>publications</c>, and what <c>delete</c> does to a share's publications.</summary>
public class UnpublishAndDeleteTests
{
    private readonly SyncWorld _world = new();

    private async Task<(Machine Machine, PublishReport First, PublishReport Second)> PublishedTwiceAsync()
    {
        Machine machine = _world.Machine("me");
        machine.Folder.WriteFile("a.txt", "a");
        machine.Folder.WriteFile("b/c.txt", "c");
        (await machine.CreateAsync()).ShouldSucceed();
        PublishReport first = (await machine.PublishAsync(new PublishShareCommand(Title: "first"))).ShouldSucceed();
        PublishReport second = (await machine.PublishAsync(new PublishShareCommand(Title: "second"))).ShouldSucceed();
        return (machine, first, second);
    }

    [Fact]
    public async Task Unpublish_Should_DeleteIndexHtmlFirst_ThenTheRest_ThenTheRecord()
    {
        (Machine machine, PublishReport first, PublishReport second) = await PublishedTwiceAsync();
        var layout = PublicationLayout.For(first.PublishId);
        int before = _world.Publishing.Operations.Count;

        UnpublishReport report = (await machine.UnpublishAsync(first.PublishId.Value)).ShouldSucceed();

        _world.Publishing.Operations.Skip(before).Take(2).ShouldBe([$"delete:{layout.Index}", $"delete-prefix:{layout.Prefix}"]);
        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith(layout.Prefix, StringComparison.Ordinal));
        _world.Publishing.BlobNames.ShouldContain(PublicationLayout.For(second.PublishId).Index);
        report.BlobsDeleted.ShouldBeGreaterThan(5);
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Select(p => p.Title).ShouldBe(["second"]);
    }

    [Fact]
    public async Task Unpublish_Should_SayWhenThereIsNoSuchPublication()
    {
        (Machine machine, _, _) = await PublishedTwiceAsync();

        (await machine.UnpublishAsync(PublishId.New().Value)).ShouldFail().Code.ShouldBe("Publish.NotFound");
        (await machine.UnpublishAsync("not-an-id")).ShouldFail().ShouldBeOfType<SharedKernel.ValidationError>().Errors.Single().Code.ShouldBe("Publish.InvalidId");
    }

    [Fact]
    public async Task Unpublish_Should_RefuseAPublicationInAnotherAccount()
    {
        (Machine machine, PublishReport first, _) = await PublishedTwiceAsync();
        _world.Publishing = new InMemoryPublishTarget(_world.Store, "someotheracct");

        (await machine.UnpublishAsync(first.PublishId.Value)).ShouldFail().Code.ShouldBe("Publish.OtherAccount");
        _world.Publishing.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unpublish_Should_FinishAnInterruptedPublication()
    {
        Machine machine = _world.Machine("me");
        machine.Folder.WriteFile("a.txt", "a");
        (await machine.CreateAsync()).ShouldSucceed();
        _world.Publishing.FailOn = name => name.EndsWith("index.html", StringComparison.Ordinal) && name.StartsWith("p/", StringComparison.Ordinal) ? Domain.Storage.StorageErrors.Unreachable : null;
        (await machine.PublishAsync()).ShouldFail();
        _world.Publishing.FailOn = null;
        Publication failed = (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single();

        (await machine.UnpublishAsync(failed.Id.Value)).ShouldSucceed();

        (await machine.PublicationsAsync()).ShouldSucceed().Publications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publications_Should_BeEmpty_ForAShareThatNeverPublished()
    {
        Machine machine = _world.Machine("me");
        machine.Folder.WriteFile("a.txt", "a");
        (await machine.CreateAsync()).ShouldSucceed();

        (await machine.PublicationsAsync()).ShouldSucceed().Publications.ShouldBeEmpty();
        (await _world.Machine("other").PublicationsAsync(machine.ShareId.ToString())).ShouldSucceed().Publications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_Should_RefuseWithoutThePublishingSettings_WhenTheShareHasPublications_AndChangeNothing()
    {
        (Machine machine, _, _) = await PublishedTwiceAsync();
        _world.Publishing.LocationError = ConfigurationErrors.Missing(PublishErrors.PublishConnectionStringVariable);
        int before = _world.Publishing.Operations.Count;

        Domain.Shares.ShareId shareId = machine.ShareId;
        SharedKernel.Error error = (await machine.DeleteAsync()).ShouldFail();

        error.Code.ShouldBe("Publish.PublicationsNeedSettings");
        error.Type.ShouldBe(SharedKernel.ErrorType.Validation);
        error.Description.ShouldContain("2 publication(s)");
        (await _world.HeadAsync(shareId)).IsDeleted.ShouldBeFalse();
        _world.Publishing.Operations.Count.ShouldBe(before);
        machine.State.State.ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_Should_UnpublishEverything_First_ThenDeleteTheShareAndItsRecords()
    {
        (Machine machine, PublishReport first, PublishReport second) = await PublishedTwiceAsync();
        Domain.Shares.ShareId shareId = machine.ShareId;

        DeleteShareResponse deleted = (await machine.DeleteAsync()).ShouldSucceed();

        deleted.PublicationsRemoved.ShouldBe(2);
        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith("p/", StringComparison.Ordinal));
        _world.Publishing.Operations.ShouldContain($"delete:{PublicationLayout.For(first.PublishId).Index}");
        _world.Publishing.Operations.ShouldContain($"delete:{PublicationLayout.For(second.PublishId).Index}");
        (await _world.Store.GetPublicationsAsync(shareId, SyncWorld.Ct)).Value.ShouldBeNull();
        (await _world.HeadAsync(shareId)).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_Should_NotNeedThePublishingSettings_WhenThereAreNoPublications()
    {
        Machine machine = _world.Machine("me");
        machine.Folder.WriteFile("a.txt", "a");
        (await machine.CreateAsync()).ShouldSucceed();
        _world.Publishing.LocationError = ConfigurationErrors.Missing(PublishErrors.PublishConnectionStringVariable);

        (await machine.DeleteAsync()).ShouldSucceed().PublicationsRemoved.ShouldBe(0);
    }
}
