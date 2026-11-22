using MySync.Application.Abstractions.Environment;

namespace MySync.Testing;

/// <summary>A fixed identity. <c>user@host</c> splits into <see cref="UserName"/> and <see cref="HostName"/>; a bare name is the host.</summary>
public sealed class FixedMachineIdentity(string name) : IMachineIdentity
{
    public string Name => name;

    public string HostName { get; init; } = name.Contains('@', StringComparison.Ordinal) ? name[(name.IndexOf('@', StringComparison.Ordinal) + 1)..] : name;

    public string UserName { get; init; } = name.Contains('@', StringComparison.Ordinal) ? name[..name.IndexOf('@', StringComparison.Ordinal)] : "tester";

    public string OperatingSystem { get; init; } = "TestOS 1.0 (test-x64)";

    public string MysyncVersion { get; init; } = "0.0.0-test";
}
