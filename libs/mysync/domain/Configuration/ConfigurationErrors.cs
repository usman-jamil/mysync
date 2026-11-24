using SharedKernel;

namespace MySync.Domain.Configuration;

/// <summary>
/// Configuration comes from environment variables only. These errors name the variable and
/// never echo its value — it may be a connection string.
/// </summary>
public static class ConfigurationErrors
{
    public static Error Missing(string variableName) => Error.Validation(
        "Configuration.Missing",
        $"The environment variable {variableName} is not set.");

    /// <summary>The variable is set but unusable. <paramref name="reason"/> must not contain the value.</summary>
    public static Error Invalid(string variableName, string reason) => Error.Validation(
        "Configuration.Invalid",
        $"The environment variable {variableName} is not valid: {reason}");

    public static Error UnknownProvider(string provider) => Error.Validation(
        "Configuration.UnknownProvider",
        $"MYSYNC_PROVIDER '{provider}' is not a supported storage provider. Supported: azure-blob, s3.");

    /// <summary>
    /// No credentials anywhere the provider looks (S3: the standard AWS chain). Code
    /// <c>Configuration.Missing</c> like a missing variable: exit 2, and never a prompt.
    /// </summary>
    public static Error NoCredentials(string provider, string sources) => Error.Validation(
        "Configuration.Missing",
        $"No credentials for {provider} were found. mysync reads them, never asks for them, from: {sources}.");

    /// <summary>The bucket named by <paramref name="variable"/> doesn't exist (or is in another partition).</summary>
    public static Error BucketNotFound(string variable, string bucket, string region) => Error.Validation(
        "Configuration.BucketNotFound",
        $"The bucket '{bucket}' ({variable}) doesn't exist in {region}. Check the bucket name and region.");

    /// <summary>The bucket is in another region than the one configured; <paramref name="actual"/> when the service said which.</summary>
    public static Error WrongRegion(string bucket, string configured, string? actual) => Error.Validation(
        "Configuration.WrongRegion",
        actual is null
            ? $"The bucket '{bucket}' isn't in {configured}, the region mysync is configured with (MYSYNC_S3_REGION or AWS_REGION). Set the bucket's region."
            : $"The bucket '{bucket}' is in {actual}, not {configured}. Set MYSYNC_S3_REGION={actual} (or AWS_REGION).");
}
