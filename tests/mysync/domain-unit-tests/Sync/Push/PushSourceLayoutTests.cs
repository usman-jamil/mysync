using MySync.Domain.Sync.Push;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Sync.Push;

/// <summary>Every row of "what gets pushed, and where it lands", and every way two arguments clash.</summary>
public class PushSourceLayoutTests
{
    private static PushSource File(string argument, string name) => new(argument, PushSourceKind.File, name);

    private static PushSource Folder(string argument, string name) => new(argument, PushSourceKind.Folder, name);

    [Fact]
    public void NoArguments_Should_PutTheCurrentFoldersContentsAtTheTargetRoot()
    {
        PushSourceRoot root = PushSourceLayout.CurrentFolder("/srv/app");

        root.Root.ShouldBeNull();
        root.Source.Kind.ShouldBe(PushSourceKind.Folder);
        root.Source.Argument.ShouldBe("/srv/app");
    }

    [Fact]
    public void AFile_Should_LandUnderItsName()
    {
        IReadOnlyList<PushSourceRoot> roots = PushSourceLayout.Map([File("error.log", "error.log")]).Value;

        roots.Single().Root!.Value.ShouldBe("error.log");
    }

    [Fact]
    public void AFolder_Should_KeepItsOwnName()
    {
        IReadOnlyList<PushSourceRoot> roots = PushSourceLayout.Map([Folder("/var/log/app", "app")]).Value;

        roots.Single().Root!.Value.ShouldBe("app");
    }

    [Fact]
    public void AFolderAndAFile_Should_LandSideBySide_InArgumentOrder()
    {
        IReadOnlyList<PushSourceRoot> roots = PushSourceLayout.Map([Folder("/var/log/app", "app"), File("/etc/app/config.yml", "config.yml")]).Value;

        roots.Select(root => root.Root!.Value).ShouldBe(["app", "config.yml"]);
        roots.Select(root => root.Source.Kind).ShouldBe([PushSourceKind.Folder, PushSourceKind.File]);
    }

    [Fact]
    public void Names_Should_BeNfc()
    {
        PushSourceLayout.Map([File("x", "café.txt")]).Value.Single().Root!.Value.ShouldBe("café.txt");
    }

    [Theory]
    [InlineData("/var/log/app", "app", "/srv/app", "app", "app")] // two folders called app
    [InlineData("app", "app", "other/app", "app", "app")] // a file and a folder of the same name
    [InlineData("a.txt", "a.txt", "a.txt", "a.txt", "a.txt")] // the same path twice
    [InlineData("x/café", "café", "y/café", "café", "café")] // equal once NFC
    public void TwoArgumentsWithOneName_Should_BeADuplicateSource(string first, string firstName, string second, string secondName, string name)
    {
        SharedKernel.Error error = PushSourceLayout.Map([Folder(first, firstName), File(second, secondName)]).Error;

        error.Code.ShouldBe("Push.DuplicateSource");
        error.Description.ShouldContain(name);
        error.Description.ShouldContain(first);
        error.Description.ShouldContain(second);
    }

    [Fact]
    public void NamesDifferingOnlyInCase_Should_NotBeDuplicates()
    {
        // They clash only on case-insensitive machines, which is a warning at push time.
        PushSourceLayout.Map([File("a/Log.txt", "Log.txt"), File("b/log.txt", "log.txt")]).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AFilesystemRoot_Should_BeAnInvalidTarget()
    {
        PushSourceLayout.Map([Folder("/", string.Empty)]).Error.Code.ShouldBe("Push.InvalidTarget");
    }

    [Theory]
    [InlineData("back\\slash")]
    [InlineData("ctl\u0001")]
    public void ANameNoSharePathCanHold_Should_BeAnInvalidTarget(string name)
    {
        PushSourceLayout.Map([File(name, name)]).Error.Code.ShouldBe("Push.InvalidTarget");
    }
}
