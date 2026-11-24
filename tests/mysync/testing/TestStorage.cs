using System.Globalization;

namespace MySync.Testing;

/// <summary>
/// The real storage the integration and end-to-end suites run against (CLAUDE.md §14): a
/// test-only Azure storage account and a test-only S3 bucket, named by <c>MYSYNC_TEST_*</c>
/// variables. There is no emulator. Without the variables a suite <b>skips</b> (a developer's
/// machine); with <see cref="RequireVariable"/> set it <b>fails</b> instead (CI), so missing
/// secrets can't pass as green.
/// </summary>
/// <remarks>
/// The developer's own <c>AZURE_STORAGE_CONNECTION_STRING</c>, <c>MYSYNC_S3_BUCKET</c> and the like
/// are never read: tests touch only storage named for tests.
/// </remarks>
public static class TestStorage
{
    public const string RequireVariable = "MYSYNC_TEST_REQUIRE_STORAGE";

    /// <summary>Whether missing test storage fails the run (CI) rather than skipping (a developer's machine).</summary>
    public static bool Required =>
        Environment.GetEnvironmentVariable(RequireVariable) is { } value
        && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>When this run started; supplied storage outlives it, so fixtures remove what was written since.</summary>
    public static DateTimeOffset RunStartedUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>
    /// <c>t-&lt;8 hex&gt;-</c>: starts every container (Azure) and key prefix (S3) this run creates, so the
    /// fixtures can delete exactly what this run wrote.
    /// </summary>
    public static string RunPrefix { get; } = "t-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + "-";

    /// <summary>
    /// Skips the test (or fails it, with <see cref="RequireVariable"/>): the storage it needs isn't
    /// configured. Never returns; typed so a property can end in it.
    /// </summary>
    public static T Unavailable<T>(string what, params string[] variables)
    {
        string message = $"{what} isn't configured: set {string.Join(", ", variables)} (see .github/workflows/ci.yml for what CI uses).";
        if (Required)
        {
            throw new InvalidOperationException(message + $" {RequireVariable} is set, so this fails instead of skipping.");
        }

        throw Xunit.Sdk.SkipException.ForSkip(message);
    }

    internal static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
