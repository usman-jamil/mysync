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

    public static Error UnknownProvider(string provider) => Error.Validation(
        "Configuration.UnknownProvider",
        $"MYSYNC_PROVIDER '{provider}' is not a supported storage provider. Supported: azure-blob.");
}
