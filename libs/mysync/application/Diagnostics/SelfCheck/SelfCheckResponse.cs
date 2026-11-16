namespace MySync.Application.Diagnostics.SelfCheck;

public sealed record SelfCheckResponse(IReadOnlyList<SelfCheckResult> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);
}

public sealed record SelfCheckResult(string Name, bool Passed, string Detail);
