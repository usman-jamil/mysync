using MySync.Application.Abstractions.Storage;
using MySync.Testing;
using MySync.Testing.Contracts;

namespace MySync.Application.UnitTests.Storage;

/// <summary>The fake against the contract Azurite also meets.</summary>
public sealed class InMemoryAliasStoreContractTests : AliasStoreContractTests
{
    protected override Task<IAliasStore> CreateStoreAsync(int? pageSize = null) => Task.FromResult<IAliasStore>(new InMemoryAliasStore());
}
