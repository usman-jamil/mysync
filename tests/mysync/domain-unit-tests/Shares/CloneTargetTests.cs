using MySync.Domain.Shares;
using MySync.Domain.Shares.Clone;
using SharedKernel;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Shares;

/// <summary><c>mysync clone</c>'s folder choice (CLAUDE.md §9): the folder given, or one named after the share, here.</summary>
public class CloneTargetTests
{
    private static readonly string Current = OperatingSystem.IsWindows() ? @"C:\work" : "/home/usman/work";

    private static ShareName Name(string name) => ShareName.Create(name).Value;

    [Fact]
    public void AShareGivenByName_Should_GetAFolderOfThatName_Here()
    {
        string target = CloneTarget.Choose(ShareReference.For(Name("app-config")), null, null, Current).Value;

        target.ShouldBe(System.IO.Path.Combine(Current, "app-config"));
    }

    [Fact]
    public void AShareGivenByName_Should_KeepTheTypedName_WhateverNameIsPassed()
    {
        string target = CloneTarget.Choose(ShareReference.For(Name("app-config")), Name("other"), null, Current).Value;

        target.ShouldBe(System.IO.Path.Combine(Current, "app-config"));
    }

    [Fact]
    public void AShareGivenById_Should_BeNamedAfterItsName()
    {
        string target = CloneTarget.Choose(ShareReference.For(Share()), Name("next-wave"), null, Current).Value;

        target.ShouldBe(System.IO.Path.Combine(Current, "next-wave"));
    }

    [Fact]
    public void AShareGivenById_WithNoName_Should_NeedAFolder()
    {
        Result<string> target = CloneTarget.Choose(ShareReference.For(Share()), null, null, Current);

        target.Error.Code.ShouldBe("Clone.FolderRequired");
        target.Error.Type.ShouldBe(ErrorType.Validation); // exit 2
        target.Error.Description.ShouldBe($"This share has no name. Say where to clone it: mysync clone {ShareIdText} <folder>");
    }

    [Fact]
    public void AShareGivenById_WithNoName_Should_UseTheFolderGiven()
    {
        string target = CloneTarget.Choose(ShareReference.For(Share()), null, "deploy", Current).Value;

        target.ShouldBe(System.IO.Path.Combine(Current, "deploy"));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("nested/deeper/config")]
    [InlineData("config/")]
    [InlineData("./config")]
    public void ARelativeFolder_Should_BeTakenFromTheCurrentFolder(string folder)
    {
        string target = CloneTarget.Choose(ShareReference.For(Name("app-config")), null, folder, Current).Value;

        target.ShouldBe(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(Current, folder))));
    }

    [Fact]
    public void AParentRelativeFolder_Should_BeResolved()
    {
        string target = CloneTarget.Choose(ShareReference.For(Name("app-config")), null, System.IO.Path.Combine("..", "elsewhere"), Current).Value;

        target.ShouldBe(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Current)!, "elsewhere"));
    }

    [Fact]
    public void AnAbsoluteFolder_Should_BeUsedAsItIs()
    {
        string absolute = OperatingSystem.IsWindows() ? @"D:\deploy\config" : "/srv/deploy/config";

        CloneTarget.Choose(ShareReference.For(Name("app-config")), null, absolute, Current).Value.ShouldBe(absolute);
    }

    [Fact]
    public void TheCurrentFolder_Should_BeAllowedAsTheFolder()
    {
        CloneTarget.Choose(ShareReference.For(Name("app-config")), null, ".", Current).Value.ShouldBe(Current);
    }

    [Fact]
    public void FirstName_Should_BeTheFirstInOrdinalOrder_OrNullForNone()
    {
        CloneTarget.FirstName([Name("zeta"), Name("alpha-2"), Name("alpha")]).ShouldBe(Name("alpha"));
        CloneTarget.FirstName([]).ShouldBeNull();
    }
}
