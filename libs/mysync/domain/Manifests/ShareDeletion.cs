namespace MySync.Domain.Manifests;

/// <summary>A deleted share's marker (<c>mysync delete</c>): who deleted it, and when (UTC).</summary>
public sealed record ShareDeletion(DateTimeOffset DeletedUtc, string DeletedBy);
