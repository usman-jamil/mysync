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
        { StorageErrors.UnauthorizedBecause("Check the AWS credentials."), "Storage.Unauthorized" }, // exit 8
        { StorageErrors.ClockSkew(12), "Storage.ClockSkew" }, // exit 2
        { MySync.Domain.Publishing.PublishErrors.UnauthorizedBecause("Check the AWS credentials."), "Publish.Unauthorized" }, // exit 8
        { MySync.Domain.Diffing.DiffErrors.VersionNotStored("a.txt", "remote"), "Diff.VersionNotStored" }, // exit 8
        { StorageErrors.UnsupportedEncoding("br"), "Storage.UnsupportedEncoding" }, // exit 8
        { StorageErrors.CorruptObject("shares/x/objects/ab/ab", "invalid gzip"), "Storage.CorruptObject" },
        { MySync.Domain.Sync.Push.PushErrors.SourceIsTheShare("/srv/app", "incident-logs"), "Push.SourceIsTheShare" }, // exit 4
        { MySync.Domain.Sync.Push.PushErrors.TargetIsAFile("web01"), "Push.TargetIsAFile" }, // exit 4
        { MySync.Domain.Sync.Pull.PullErrors.NoPushes("app-logs"), "Pull.NoPushes" }, // exit 8
        { MySync.Domain.Session.TuiErrors.AlreadyRunning(4242), "Tui.AlreadyRunning" }, // exit 6
        { MySync.Domain.Session.SessionErrors.Busy("sync"), "Session.Busy" },
        { MySync.Domain.Sync.Pull.PullErrors.DestinationOccupied(2, ["a.log", "b.log"], overwrite: false), "Pull.DestinationOccupied" }, // exit 4
        { MySync.Domain.Sync.Pull.PullErrors.DestinationIsTheShare("/home/u/logs", "app-logs"), "Pull.DestinationIsTheShare" }, // exit 4
        { MySync.Domain.Shares.Clone.CloneErrors.TargetIsAFile("/home/u/app-config"), "Clone.TargetIsAFile" }, // exit 4
        { MySync.Domain.Shares.Clone.CloneErrors.InsideAShare("/home/u/work", Share()), "Clone.InsideAShare" }, // exit 4
        { MySync.Domain.Shares.Clone.CloneErrors.CannotCreate("/root/x", "Access denied"), "Clone.CannotCreate" }, // exit 4
        { MySync.Domain.Shares.Clone.CloneErrors.FolderNotEmpty("/home/u/app-config"), "Shares.FolderNotEmpty" }, // exit 4
        { MySync.Domain.Shares.Clone.CloneErrors.UnsafeRoot("/"), "Shares.UnsafeRoot" }, // exit 4
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
        MySync.Domain.Sync.Push.PushErrors.InvalidSince("2x").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Pull.PullErrors.PathNotFound("x").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Session.TuiErrors.NotATerminal().Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Shares.Clone.CloneErrors.FolderRequired(Share()).Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Session.TuiErrors.TooSmall(60, 20).Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Session.SessionErrors.PushToOwnShare("app-config").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Session.SessionErrors.NothingOnDisk("x").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Pull.PullErrors.DuplicateTarget("a", "x/a", "y/a").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Pull.PullErrors.HostNotFound("web01").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Pull.PullErrors.LatestWithPaths().Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Pull.PullErrors.HostNeedsLatest().Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.InvalidPattern("--exclude", "!a", "negated").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.InvalidSize("0").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Sync.Push.PushErrors.InvalidSince("2x").Code.ShouldBe("Push.InvalidSince");
        MySync.Domain.Sync.Push.PushErrors.InvalidPattern("--exclude", "!a", "negated").Code.ShouldBe("Push.InvalidPattern");
        MySync.Domain.Sync.Push.PushErrors.InvalidSize("0").Code.ShouldBe("Push.InvalidSize");
        ConfigurationErrors.WrongRegion("team-shares", "us-east-1", "eu-west-1").Type.ShouldBe(ErrorType.Validation);
        ConfigurationErrors.BucketNotFound("MYSYNC_S3_BUCKET", "team-shares", "us-east-1").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Publishing.PublishErrors.NotPublic("team-publish", "https://team-publish.s3.eu-west-1.amazonaws.com").Type.ShouldBe(ErrorType.Validation);
        MySync.Domain.Publishing.PublishErrors.PublicListing("team-publish").Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void StorageMismatch_BetweenProviders_Should_SayWhichProviderTheFolderBelongsTo()
    {
        string description = StateErrors.StorageMismatch(
            new StorageLocation("azure-blob", "teamsyncstorage", "mysync"),
            new StorageLocation("s3", "eu-west-1", "team-shares")).Description;

        description.ShouldBe(
            "This folder belongs to azure-blob (account teamsyncstorage, container mysync), but the environment points at s3 (bucket team-shares in eu-west-1). " +
            "Nothing was changed. Set MYSYNC_PROVIDER=azure-blob and its settings (AZURE_STORAGE_CONNECTION_STRING, MYSYNC_CONTAINER) to sync this folder.");
    }

    [Fact]
    public void NotPublic_Should_GiveThePolicy_AndNameAllThreeBlockers()
    {
        string description = MySync.Domain.Publishing.PublishErrors.NotPublic("team-publish", "https://team-publish.s3.eu-west-1.amazonaws.com").Description;

        description.ShouldContain("arn:aws:s3:::team-publish/p/*");
        description.ShouldContain("arn:aws:s3:::team-publish/robots.txt");
        description.ShouldNotContain("s3:ListBucket");
        description.ShouldContain("the bucket's Block Public Access settings");
        description.ShouldContain("the account-level Block Public Access settings");
        description.ShouldContain("an organisation policy");
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
