using System.Reflection;
using System.Runtime.InteropServices;
using MySync.Application.Abstractions.Environment;

namespace MySync.Infrastructure.Identity;

/// <summary><c>user@MACHINE</c>, recorded as a manifest's <c>updatedBy</c> and shown in conflicts; and what a push records about this machine.</summary>
internal sealed class MachineIdentity : IMachineIdentity
{
    public MachineIdentity()
    {
        UserName = Part(() => System.Environment.UserName);
        HostName = Part(() => System.Environment.MachineName);
        Name = $"{UserName}@{HostName}";
    }

    public string Name { get; }

    public string HostName { get; }

    public string UserName { get; }

    public string OperatingSystem { get; } = $"{Part(() => RuntimeInformation.OSDescription.Trim())} ({RuntimeInformation.RuntimeIdentifier})";

    public string MysyncVersion { get; } = Version();

    private static string Version()
    {
        string? informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return "unknown";
        }

        // "0.8.0+3074178…": the source revision isn't part of the version.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    private static string Part(Func<string> read)
    {
        try
        {
            string value = read();
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
        catch (PlatformNotSupportedException)
        {
            return "unknown";
        }
    }
}
