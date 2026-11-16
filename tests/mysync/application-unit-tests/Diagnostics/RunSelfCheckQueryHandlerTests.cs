using MySync.Application.Diagnostics.SelfCheck;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Diagnostics;

public class RunSelfCheckQueryHandlerTests
{
    [Fact]
    public async Task Handle_Should_PassEveryCheck_OnTheTestRuntime()
    {
        var handler = new RunSelfCheckQueryHandler();

        Result<SelfCheckResponse> result = await handler.Handle(
            new RunSelfCheckQuery(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Passed.ShouldBeTrue();
        result.Value.Checks.ShouldContain(check => check.Name == "unicode-normalization");
    }
}
