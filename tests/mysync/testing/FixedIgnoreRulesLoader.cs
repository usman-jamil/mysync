using MySync.Application.Abstractions.Ignore;
using SharedKernel;

namespace MySync.Testing;

/// <summary>Hands out whatever matcher the test set; replace <see cref="Matcher"/> to change the rules between runs.</summary>
public sealed class FixedIgnoreRulesLoader(IIgnoreMatcher matcher) : IIgnoreRulesLoader
{
    public IIgnoreMatcher Matcher { get; set; } = matcher;

    public Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Result.Success(Matcher));
}
