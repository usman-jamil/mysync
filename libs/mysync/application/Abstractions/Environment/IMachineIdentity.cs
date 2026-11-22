namespace MySync.Application.Abstractions.Environment;

/// <summary>Who is syncing, recorded as the manifest's <c>updatedBy</c>, e.g. <c>usman@DEV-LAPTOP</c>.</summary>
public interface IMachineIdentity
{
    /// <summary><c>user@MACHINE</c>.</summary>
    string Name { get; }

    /// <summary>The machine name: a push's default host folder.</summary>
    string HostName { get; }

    /// <summary>The account mysync runs as (the push record's <c>user</c>).</summary>
    string UserName { get; }

    /// <summary>E.g. <c>Ubuntu 24.04.1 LTS (linux-x64)</c> (the push record's <c>os</c>).</summary>
    string OperatingSystem { get; }

    /// <summary>The running mysync's version, e.g. <c>0.8.0</c>.</summary>
    string MysyncVersion { get; }
}
