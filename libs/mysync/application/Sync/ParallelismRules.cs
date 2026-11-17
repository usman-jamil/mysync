using FluentValidation;

namespace MySync.Application.Sync;

internal static class ParallelismRules
{
    /// <summary><c>--parallel</c> is optional; when given it must be 1–64.</summary>
    public static IRuleBuilderOptions<T, int?> ValidParallelism<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(SyncOptions.MinParallelism, SyncOptions.MaxAllowedParallelism)
            .WithErrorCode("Sync.InvalidParallelism")
            .WithMessage($"--parallel must be between {SyncOptions.MinParallelism} and {SyncOptions.MaxAllowedParallelism}.");
}
