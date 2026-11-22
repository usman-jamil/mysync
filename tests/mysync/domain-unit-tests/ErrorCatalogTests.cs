using MySync.Domain.Configuration;
using MySync.Domain.Paths;
using MySync.Domain.Shares;
using MySync.Domain.State;
using MySync.Domain.Storage;
using MySync.Domain.Sync;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests;

/// <summary>
/// <c>apps/mysync/ExitCodes.cs</c> maps errors to exit codes by <see cref="Error.Code"/>, and maps
/// every <see cref="ErrorType.Validation"/> error to exit 2 before looking at the code. These
/// tests pin both halves of that contract from the domain side.
/// </summary>
public class ErrorCatalogTests
{
    public static TheoryData<Error, string> ErrorsWithTheirOwnExitCode => new()
    {
        { ShareErrors.FolderNotEmpty("/tmp/x"), "Shares.FolderNotEmpty" }, // exit 4
        { ShareErrors.AlreadyAShare(Share()), "Shares.AlreadyAShare" }, // exit 4
        { ShareErrors.UnsafeRoot("/"), "Shares.UnsafeRoot" }, // exit 4
        { SyncErrors.CommitRetriesExhausted(5), "Sync.CommitRetriesExhausted" }, // exit 5
        { StateErrors.FolderLocked, "State.FolderLocked" }, // exit 6
        { SyncErrors.MassDeleteBlocked(40, 50), "Sync.MassDeleteBlocked" }, // exit 7
        { ShareErrors.NotFound(Share()), "Shares.NotFound" }, // exit 8
        { ShareErrors.NameTaken(ShareName.Create("next-wave").Value, Share()), "Shares.NameTaken" }, // exit 4
        { ShareErrors.NameNotFound(ShareName.Create("next-wave").Value), "Shares.NameNotFound" }, // exit 8
        { ShareErrors.Incomplete(ShareName.Create("next-wave").Value, Share()), "Shares.Incomplete" }, // exit 8
        { StorageErrors.Unauthorized, "Storage.Unauthorized" }, // exit 8
        { MySync.Domain.Diffing.DiffErrors.VersionNotStored("a.txt", "remote"), "Diff.VersionNotStored" }, // exit 8
        { StorageErrors.UnsupportedEncoding("br"), "Storage.UnsupportedEncoding" }, // exit 8
        { StorageErrors.CorruptObject("shares/x/objects/ab/ab", "invalid gzip"), "Storage.CorruptObject" },
        { MySync.Domain.Sync.Push.PushErrors.SourceIsTheShare("/srv/app", "incident-logs"), "Push.SourceIsTheShare" }, // exit 4
        { MySync.Domain.Sync.Push.PushErrors.TargetIsAFile("web01"), "Push.TargetIsAFile" }, // exit 4
    };

    [Theory]
    [MemberData(nameof(ErrorsWithTheirOwnExitCode))]
    public void Codes_Should_MatchWhatExitCodesLooksFor_AndNotBeValidationErrors(Error error, string expectedCode)
    {
        error.Code.ShouldBe(expectedCode);

        // A Validation type would turn these into exit 2 before their code is looked at.
        error.Type.ShouldNotBe(ErrorType.Validation);
    }

    [Fact]
    public void UsageErrors_Should_BeValidationErrors_SoTheyExitWithTwo()
    {
        ShareErrors.InvalidId().Type.ShouldBe(ErrorType.Validation);
        ShareErrors.NameRequired(null).Type.ShouldBe(ErrorType.Validation);
        ShareErrors.InvalidName("x").Type.ShouldBe(ErrorType.Validation);
        ShareErrors.InvalidReference().Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Diffing.DiffErrors.PathNotFound("a.txt").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Diffing.DiffErrors.VersionMissing("a.txt", "base").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Diffing.DiffErrors.PrintNeedsOnePath.Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Diffing.DiffErrors.PrintCombined.Type.ShouldBe(ErrorType.Validation);
        PathErrors.Empty().Type.ShouldBe(ErrorType.Validation);
        ConfigurationErrors.Missing("AZURE_STORAGE_CONNECTION_STRING").Type.ShouldBe(ErrorType.Validation);
        ConfigurationErrors.Invalid("MYSYNC_CONTAINER", "too short").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.DuplicateSource("app", "/a/app", "/b/app").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.SourceNotFound("missing.log").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.InvalidTarget("x").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.CreateNeedsName().Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void StorageMismatch_Should_NameAccountsAndContainers_ButNoCredentials()
    {
        string description = StateErrors.StorageMismatch(
            new StorageLocation("azure-blob", "teamsyncstorage", "mysync"),
            new StorageLocation("azure-blob", "otheraccount", "mysync")).Description;

        description.ShouldContain("teamsyncstorage");
        description.ShouldContain("otheraccount");
        description.ShouldNotContain("AccountKey", Case.Insensitive);
    }
}
