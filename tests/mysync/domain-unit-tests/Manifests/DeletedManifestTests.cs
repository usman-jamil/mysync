using MySync.Domain.Manifests;
using MySync.Domain.Shares;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Manifests;

/// <summary>The deleted marker that <c>mysync delete</c> puts in place of HEAD.</summary>
public class DeletedManifestTests
{
    [Fact]
    public void CreateDeletedRevision_Should_BeTheNextRevision_WithNoEntries_AndWhoDeletedItWhen()
    {
        Manifest head = Ok(Manifest.CreateNew(Share(), [Entry("secret.txt", File())], Created, "usman@DEV-LAPTOP"));

        Manifest marker = Ok(head.CreateDeletedRevision(Updated, "ali@SERVER01"));

        marker.IsDeleted.ShouldBeTrue();
        marker.Revision.ShouldBe(Rev(2));
        marker.Entries.ShouldBeEmpty();
        marker.CreatedUtc.ShouldBe(Created);
        marker.UpdatedBy.ShouldBe("ali@SERVER01");
        marker.Deletion.ShouldBe(new ShareDeletion(Updated, "ali@SERVER01"));
        marker.FormatVersion.ShouldBe(Manifest.DeletedFormatVersion);
        head.FormatVersion.ShouldBe(Manifest.CurrentFormatVersion);
    }

    [Fact]
    public void ADeletedShare_Should_RefuseAnyFurtherRevision()
    {
        Manifest marker = Ok(Ok(Manifest.CreateNew(Share(), [], Created, "a")).CreateDeletedRevision(Updated, "a"));

        marker.CreateNextRevision([Entry("new.txt", File())], Updated, "b").Error.Code.ShouldBe("Shares.Deleted");
        marker.CreateDeletedRevision(Updated, "b").Error.Code.ShouldBe("Shares.Deleted");
    }

    [Fact]
    public void TheDeletedFormat_Should_StayAboveTheOrdinaryOne_SoOlderClientsRefuseIt()
    {
        // 0.2.0 and older read format 1 only. If the marker were format 1, they would read "no
        // entries" as "every file was deleted remotely" and delete their local copies.
        Manifest.CurrentFormatVersion.ShouldBe(1);
        Manifest.DeletedFormatVersion.ShouldBeGreaterThan(Manifest.CurrentFormatVersion);
        Manifest.MaxReadableFormatVersion.ShouldBeGreaterThanOrEqualTo(Manifest.DeletedFormatVersion);
    }

    [Fact]
    public void Restore_Should_RebuildAMarker_FromFormatTwoWithADeletion()
    {
        Manifest marker = Ok(Manifest.Restore(2, Share(), Rev(5), Created, Updated, "a", [], new ShareDeletion(Updated, "a")));

        marker.IsDeleted.ShouldBeTrue();
        marker.Deletion!.DeletedBy.ShouldBe("a");
    }

    [Fact]
    public void Restore_Should_RejectAMarkerThatDoesntAddUp()
    {
        ShareDeletion deletion = new(Updated, "a");

        Manifest.Restore(2, Share(), Rev(5), Created, Updated, "a", []).Error.Code.ShouldBe("Shares.InvalidManifest");
        Manifest.Restore(1, Share(), Rev(5), Created, Updated, "a", [], deletion).Error.Code.ShouldBe("Shares.InvalidManifest");
        Manifest.Restore(2, Share(), Rev(5), Created, Updated, "a", [Entry("left-behind.txt", File())], deletion).Error.Code.ShouldBe("Shares.InvalidManifest");
        Manifest.Restore(4, Share(), Rev(5), Created, Updated, "a", []).Error.ShouldBe(ShareErrors.UnsupportedFormat(4));
    }

    [Fact]
    public void TheDeletedError_Should_SayWhoAndWhen_AndBeANotFound_ForExitCodeEight()
    {
        Error error = ShareErrors.Deleted(Share(), new ShareDeletion(Updated, "usman@DEV-LAPTOP"));

        error.Code.ShouldBe("Shares.Deleted");
        error.Type.ShouldBe(ErrorType.NotFound);
        error.Description.ShouldContain("deleted by usman@DEV-LAPTOP on 2026-11-16 09:02 UTC");
        error.Description.ShouldContain("Nothing was changed here");
    }

    [Fact]
    public void TheDeletedError_Should_EscapeACraftedName()
    {
        ShareErrors.Deleted(Share(), new ShareDeletion(Updated, "evil\u001b[2J")).Description.ShouldNotContain("\u001b");
    }
}
