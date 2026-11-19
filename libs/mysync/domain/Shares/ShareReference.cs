using SharedKernel;

namespace MySync.Domain.Shares;

/// <summary>
/// What someone typed where a share is expected (<c>sync &lt;share&gt;</c>, <c>delete</c>, <c>info</c>,
/// <c>--share</c>): a share ID if it parses as one, otherwise a <see cref="ShareName"/>. The one parser
/// every command uses. A name still has to be resolved to its ID through its alias.
/// </summary>
public sealed record ShareReference
{
    private ShareReference(ShareId? id, ShareName? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Set when the input was a share ID.</summary>
    public ShareId? Id { get; }

    /// <summary>Set when the input was a name.</summary>
    public ShareName? Name { get; }

    public static ShareReference For(ShareId id) => new(id ?? throw new ArgumentNullException(nameof(id)), null);

    public static ShareReference For(ShareName name) => new(null, name ?? throw new ArgumentNullException(nameof(name)));

    public static Result<ShareReference> Parse(string? value)
    {
        if (ShareId.TryParse(value?.Trim(), out ShareId? id))
        {
            return For(id);
        }

        Result<ShareName> name = ShareName.Create(value);
        return name.IsSuccess ? For(name.Value) : Result.Failure<ShareReference>(ShareErrors.InvalidReference());
    }

    public override string ToString() => Id?.ToString() ?? Name!.Value;
}
