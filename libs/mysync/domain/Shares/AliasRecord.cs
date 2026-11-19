namespace MySync.Domain.Shares;

/// <summary>
/// One share name: the alias blob <c>aliases/&lt;name&gt;</c>, the only record of names. Manifests
/// and <c>.mysynctool</c> don't hold names.
/// </summary>
public sealed record AliasRecord(ShareName Name, ShareId ShareId, DateTimeOffset CreatedUtc, string CreatedBy);
