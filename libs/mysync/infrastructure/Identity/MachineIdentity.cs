using MySync.Application.Abstractions.Environment;

namespace MySync.Infrastructure.Identity;

/// <summary><c>user@MACHINE</c>, recorded as a manifest's <c>updatedBy</c> and shown in conflicts.</summary>
internal sealed class MachineIdentity : IMachineIdentity
{
    public string Name { get; } = $"{Part(() => System.Environment.UserName)}@{Part(() => System.Environment.MachineName)}";

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
