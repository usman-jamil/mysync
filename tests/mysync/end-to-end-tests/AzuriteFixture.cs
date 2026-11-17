using MySync.EndToEndTests;
using MySync.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(AzuriteFixture))]

namespace MySync.EndToEndTests;

/// <summary>One Azurite for the whole assembly. Inject it into a test class's constructor.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private AzuriteEnvironment? _environment;

    public string ConnectionString =>
        _environment?.ConnectionString ?? throw new InvalidOperationException("Azurite has not started.");

    public async ValueTask InitializeAsync() =>
        _environment = await AzuriteEnvironment.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }
    }
}
