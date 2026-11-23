using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Sync.Pull;
using MySync.Domain.Sync.Push;
using Shouldly;
using SharedKernel;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Sync.Pull;

/// <summary>What <c>mysync pull</c> takes from HEAD and where it lands: share paths, the whole share, and <c>--latest</c>.</summary>
public class PullSelectionTests
{
    // A share people sync (docs/, config/) that servers also push into (web01/, web02/, db01/).
    private static readonly Dictionary<SharePath, ManifestEntry> Share = new()
    {
        [Path("docs/readme.md")] = File(),
        [Path("config/app.yml")] = File(),
        [Path("web01/20261122-235512Z/app/server.log")] = File(),
        [Path("web01/20261123-004512Z/app/server.log")] = File(),
        [Path("web01/20261123-004512Z/app/sub/b.log")] = File(),
        [Path("web01/20261123-004512Z/empty")] = DirectoryEntry.Instance,
        [Path("web01/20261123-004512Z/_mysync-push.json")] = File(),
        [Path("web012/notes.txt")] = File(),
        [Path("web02/20261123-004630Z/a.log")] = File(),
        [Path("web02/20261123-004630Z-2/a.log")] = File(),
        [Path("db01/20261123-003015Z/dump.sql")] = File(),
        [Path("logs")] = DirectoryEntry.Instance,
    };

    private static PullSelection Select(params string[] paths) => PullSelection.Select(Share, [.. paths.Select(Path)]).Value;

    private static Error SelectError(params string[] paths) => PullSelection.Select(Share, [.. paths.Select(Path)]).Error;

    private static string[] Locals(PullSelection selection) => [.. selection.Files.Select(file => file.LocalPath.Value)];

    [Fact]
    public void NoPaths_Should_PutTheSharesContentsAtTheDestinationRoot_SyncedFilesAndPushedFoldersAlike()
    {
        PullSelection selection = Select();

        selection.Roots.ShouldBe([new PullRoot(string.Empty, string.Empty)]);
        Locals(selection).ShouldBe([.. Share.Where(pair => pair.Value is FileEntry).Select(pair => pair.Key.Value).Order(StringComparer.Ordinal)]);
        selection.Files.ShouldAllBe(file => file.LocalPath == file.SharePath);
        selection.EmptyFolders.Select(path => path.Value).ShouldBe(["logs", "web01/20261123-004512Z/empty"]);
    }

    [Fact]
    public void AFolder_Should_LandUnderItsOwnName()
    {
        PullSelection selection = Select("web01/20261123-004512Z");

        selection.Roots.ShouldBe([new PullRoot("web01/20261123-004512Z", "20261123-004512Z")]);
        Locals(selection).ShouldBe(["20261123-004512Z/_mysync-push.json", "20261123-004512Z/app/server.log", "20261123-004512Z/app/sub/b.log"]);
        selection.EmptyFolders.Select(path => path.Value).ShouldBe(["20261123-004512Z/empty"]);
        selection.Files[1].SharePath.Value.ShouldBe("web01/20261123-004512Z/app/server.log");
    }

    [Fact]
    public void AFile_Should_LandAtTheRootUnderItsName()
    {
        PullSelection selection = Select("web01/20261123-004512Z/app/server.log");

        Locals(selection).ShouldBe(["server.log"]);
        selection.Roots.ShouldBe([new PullRoot("web01/20261123-004512Z/app/server.log", "server.log", IsFile: true)]);
        selection.EmptyFolders.ShouldBeEmpty();
    }

    [Fact]
    public void AFolderAndAFile_Should_EachLandUnderTheirName()
    {
        PullSelection selection = Select("web01", "config/app.yml");

        Locals(selection).ShouldBe([
            "app.yml",
            "web01/20261122-235512Z/app/server.log",
            "web01/20261123-004512Z/_mysync-push.json",
            "web01/20261123-004512Z/app/server.log",
            "web01/20261123-004512Z/app/sub/b.log"]);
    }

    [Fact]
    public void ANestedFolder_Should_KeepOnlyItsOwnName()
    {
        Locals(Select("web01/20261123-004512Z/app/sub")).ShouldBe(["sub/b.log"]);
    }

    [Fact]
    public void ANameThatOnlyStartsLikeAFolder_Should_NotMatchIt()
    {
        SelectError("web0").Code.ShouldBe("Pull.PathNotFound");
        Locals(Select("web01")).ShouldNotContain("web01/notes.txt");
        Locals(Select("web012")).ShouldBe(["web012/notes.txt"]);
    }

    [Fact]
    public void AnEmptyFolderEntry_Should_BeAFolderToCreate()
    {
        PullSelection selection = Select("logs");

        selection.Files.ShouldBeEmpty();
        selection.EmptyFolders.Select(path => path.Value).ShouldBe(["logs"]);
        selection.IsEmpty.ShouldBeFalse();
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("docs/readme")]
    [InlineData("docs/readme.md/x")]
    public void APathNotInTheShare_Should_BeRefused(string path)
    {
        Error error = SelectError(path);

        error.Code.ShouldBe("Pull.PathNotFound");
        error.Type.ShouldBe(ErrorType.Validation);
        error.Description.ShouldContain(path);
    }

    [Theory]
    [InlineData("web01/20261123-004512Z/app/server.log", "web01/20261122-235512Z/app/server.log", "server.log")]
    [InlineData("web01/20261123-004512Z/app", "web01/20261122-235512Z/app", "app")]
    [InlineData("docs", "docs", "docs")]
    public void TwoPathsLandingAtOneName_Should_BeRefused(string first, string second, string name)
    {
        Error error = SelectError(first, second);

        error.Code.ShouldBe("Pull.DuplicateTarget");
        error.Description.ShouldContain($"'{name}'");
    }

    [Fact]
    public void Latest_Should_TakeEachHostsNewestPushFolder_KeepingTheHost()
    {
        PullSelection selection = PullSelection.Latest(Share, [], "app-logs").Value;

        selection.Roots.Select(root => root.SharePath).ShouldBe(["db01/20261123-003015Z", "web01/20261123-004512Z", "web02/20261123-004630Z-2"]);
        selection.Roots.ShouldAllBe(root => root.LocalPath == root.SharePath);
        Locals(selection).ShouldBe([
            "db01/20261123-003015Z/dump.sql",
            "web01/20261123-004512Z/_mysync-push.json",
            "web01/20261123-004512Z/app/server.log",
            "web01/20261123-004512Z/app/sub/b.log",
            "web02/20261123-004630Z-2/a.log"]);
        selection.EmptyFolders.Select(path => path.Value).ShouldBe(["web01/20261123-004512Z/empty"]);
    }

    [Fact]
    public void Latest_Should_IgnoreTopLevelFoldersThatHoldNoPushFolder()
    {
        PullSelection selection = PullSelection.Latest(Share, [], "app-logs").Value;

        selection.Roots.Select(root => root.SharePath).ShouldNotContain(path => path.StartsWith("docs", StringComparison.Ordinal) || path.StartsWith("web012", StringComparison.Ordinal));
    }

    [Fact]
    public void Latest_Should_OrderPushesOfOneSecondBySuffix()
    {
        Dictionary<SharePath, ManifestEntry> share = new()
        {
            [Path("h/20261123-004512Z-3/a")] = File(),
            [Path("h/20261123-004512Z-10/a")] = File(),
            [Path("h/20261123-004512Z/a")] = File(),
        };

        PullSelection.Latest(share, [], "s").Value.Roots.ShouldHaveSingleItem().SharePath.ShouldBe("h/20261123-004512Z-10");
    }

    [Fact]
    public void Latest_WithOneHost_Should_TakeOnlyThatHost()
    {
        Locals(PullSelection.Latest(Share, ["web02"], "app-logs").Value).ShouldBe(["web02/20261123-004630Z-2/a.log"]);
    }

    [Fact]
    public void Latest_WithHosts_Should_MatchThemIgnoringCase()
    {
        PullSelection selection = PullSelection.Latest(Share, ["WEB01", "db01"], "app-logs").Value;

        selection.Roots.Select(root => root.SharePath).ShouldBe(["db01/20261123-003015Z", "web01/20261123-004512Z"]);
    }

    [Fact]
    public void Latest_WithAHostThatHasNoPushFolder_Should_BeRefused()
    {
        Error error = PullSelection.Latest(Share, ["web01", "docs"], "app-logs").Error;

        error.Code.ShouldBe("Pull.HostNotFound");
        error.Type.ShouldBe(ErrorType.Validation);
        error.Description.ShouldContain("'docs'");
    }

    [Fact]
    public void Latest_InAShareWithoutPushes_Should_SaySo()
    {
        Dictionary<SharePath, ManifestEntry> share = new()
        {
            [Path("docs/readme.md")] = File(),
            [Path("web01/notes/a.txt")] = File(),
            [Path("web01/20261123-004512Z")] = File(), // a file, not a push folder
        };

        Error error = PullSelection.Latest(share, [], "team-docs").Error;

        error.Code.ShouldBe("Pull.NoPushes");
        error.Type.ShouldBe(ErrorType.NotFound);
        error.Description.ShouldBe("team-docs has no pushed folders (<host>/<timestamp>/).");
    }

    [Fact]
    public void Latest_Should_CountAnEmptyPushFolder()
    {
        Dictionary<SharePath, ManifestEntry> share = new() { [Path("web01/20261123-004512Z")] = DirectoryEntry.Instance };

        PullSelection selection = PullSelection.Latest(share, [], "s").Value;

        selection.EmptyFolders.Select(path => path.Value).ShouldBe(["web01/20261123-004512Z"]);
    }

    [Fact]
    public void AnEmptyShare_Should_SelectNothing()
    {
        PullSelection.Select(new Dictionary<SharePath, ManifestEntry>(), []).Value.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void PushFolderNames_Should_GiveTheirNumber()
    {
        PushTarget.TryParseFolderName("20261123-004512Z", out _, out int first).ShouldBeTrue();
        PushTarget.TryParseFolderName("20261123-004512Z-12", out _, out int twelfth).ShouldBeTrue();

        (first, twelfth).ShouldBe((1, 12));
    }
}
