namespace MySync.Application.Abstractions.Environment;

/// <summary>Who is syncing, recorded as the manifest's <c>updatedBy</c>, e.g. <c>usman@DEV-LAPTOP</c>.</summary>
public interface IMachineIdentity
{
    string Name { get; }
}
