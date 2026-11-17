using MySync.Application.Abstractions.Environment;

namespace MySync.Testing;

public sealed class FixedMachineIdentity(string name) : IMachineIdentity
{
    public string Name => name;
}
