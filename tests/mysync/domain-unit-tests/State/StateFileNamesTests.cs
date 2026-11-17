using MySync.Domain.State;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.State;

public class StateFileNamesTests
{
    [Fact]
    public void AlwaysIgnored_Should_NotContainTheIgnoreFile_BecauseItIsSynced()
    {
        StateFileNames.AlwaysIgnored.ShouldNotContain(StateFileNames.IgnoreFile);
    }

    [Fact]
    public void AlwaysIgnored_Should_ContainTheStateFile()
    {
        StateFileNames.AlwaysIgnored.ShouldContain(StateFileNames.State);
    }

    [Theory]
    [InlineData(".DS_Store", true)]
    [InlineData(".ds_store", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("._report.pdf", true)]
    [InlineData("report.pdf", false)]
    [InlineData("_report.pdf", false)]
    [InlineData(".mysyncignore", false)]
    public void IsOsClutter_Should_MatchOnlyTheKnownNames_IgnoringCase(string name, bool expected)
    {
        StateFileNames.IsOsClutter(name).ShouldBe(expected);
    }
}
