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
}
