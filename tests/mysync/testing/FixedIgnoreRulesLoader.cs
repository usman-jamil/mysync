using MySync.Application.Abstractions.Ignore;
using SharedKernel;

namespace MySync.Testing;

/// <summary>Hands out whatever matcher the test set; replace <see cref="Matcher"/> to change the rules between runs.</summary>
public sealed class FixedIgnoreRulesLoader(IIgnoreMatcher matcher) : IIgnoreRulesLoader
{
    public IIgnoreMatcher Matcher { get; set; } = matcher;

    /// <summary>Every text <see cref="Compile"/> was given, in order.</summary>
    public List<string> Compiled { get; } = [];

    public Task<Result<IIgnoreMatcher>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Result.Success(Matcher));

    public IIgnoreMatcher Compile(string rules)
    {
        lock (Compiled)
        {
            Compiled.Add(rules);
        }

        return new SimplePatternIgnoreMatcher(rules);
    }
}
