using MySync.Application.Abstractions.Storage;
using MySync.Testing;
using MySync.Testing.Contracts;

namespace MySync.Application.UnitTests.Storage;

/// <summary>Runs the shared <see cref="IShareStore"/> contract against the in-memory fake.</summary>
public sealed class InMemoryShareStoreContractTests : ShareStoreContractTests
{
    protected override Task<IShareStore> CreateStoreAsync() => Task.FromResult<IShareStore>(new InMemoryShareStore());
}
