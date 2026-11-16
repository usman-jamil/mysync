using Microsoft.Extensions.DependencyInjection;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_Should_RegisterAUtcClock()
    {
        using ServiceProvider provider = new ServiceCollection().AddInfrastructure().BuildServiceProvider();

        IDateTimeProvider clock = provider.GetRequiredService<IDateTimeProvider>();

        clock.UtcNow.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
