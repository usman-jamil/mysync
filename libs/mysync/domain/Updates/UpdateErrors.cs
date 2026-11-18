using SharedKernel;

namespace MySync.Domain.Updates;

/// <summary>
/// Why <c>mysync update</c> didn't update. In every case the installed binary is unchanged:
/// nothing is replaced until the new one has been verified and has passed its own self-check.
/// </summary>
public static class UpdateErrors
{
    public static Error InvalidVersion() => Error.Validation(
        "Update.InvalidVersion", "--version must look like 1.2.3 or 1.2.3-beta.1.");

    public static Error ReleaseNotFound(string what, string? hint = null) => Error.NotFound(
        "Update.ReleaseNotFound", $"No mysync release {what} was found.{(hint is null ? string.Empty : " " + hint)}");

    public static Error NoBuildForPlatform(ReleaseVersion version, string runtimeIdentifier) => Error.NotFound(
        "Update.NoBuildForPlatform", $"mysync {version} has no build for {runtimeIdentifier}.");

    public static Error SourceUnreachable(string detail) => Error.Problem(
        "Update.SourceUnreachable", $"Could not reach the release source: {detail}");

    public static Error SignatureMissing(ReleaseVersion version) => Error.Problem(
        "Update.SignatureMissing",
        $"mysync {version} has no {ReleaseAssets.SignatureName}, so it can't be verified. Releases before signing began must be installed by hand.");

    public static readonly Error SignatureInvalid = Error.Problem(
        "Update.SignatureInvalid",
        $"The release's {ReleaseAssets.ChecksumsName} is not signed with the mysync release key. Nothing was installed.");

    public static Error ChecksumMismatch(string asset) => Error.Problem(
        "Update.ChecksumMismatch", $"{asset} does not match its checksum. Nothing was installed.");

    public static Error NewBinaryFailed(string detail) => Error.Problem(
        "Update.NewBinaryFailed", $"The downloaded mysync didn't pass its checks on this machine ({detail}). Nothing was installed.");

    public static Error NotWritable(string path, string advice) => Error.Problem(
        "Update.NotWritable", $"mysync can't replace {path}: {advice}");

    public static Error NotInstalledBinary(string detail) => Error.Problem(
        "Update.NotInstalledBinary", $"This mysync isn't an installed release binary ({detail}), so it can't update itself.");
}
