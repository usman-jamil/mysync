using System.Text.RegularExpressions;

namespace MySync.Storage.S3;

/// <summary>
/// Masks AWS secrets in any text that might carry one (CLAUDE.md §5): the values of
/// <c>AWS_SECRET_ACCESS_KEY</c> / <c>AWS_SESSION_TOKEN</c> (and their config-file spellings), and
/// the signature, credential and security token of a presigned URL
/// (<c>X-Amz-Signature</c>, <c>X-Amz-Credential</c>, <c>X-Amz-Security-Token</c>). Applied to
/// everything from the AWS SDK before it is logged; error descriptions never include SDK text.
/// </summary>
public static partial class S3Redactor
{
    public const string Mask = "***";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = NamedSecret().Replace(text, match => match.Groups["name"].Value + match.Groups["separator"].Value + Mask);
        return PresignedQuery().Replace(redacted, match => match.Groups["name"].Value + "=" + Mask);
    }

    // AWS_SECRET_ACCESS_KEY=..., aws_session_token = ..., "SecretAccessKey": "...", SessionToken: ...
    [GeneratedRegex(
        """(?<name>AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN|AWS_SECURITY_TOKEN|aws_secret_access_key|aws_session_token|SecretAccessKey|SecretKey|SessionToken)(?<separator>['"]?\s*[=:]\s*['"]?)[^\s"',;&]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedSecret();

    // ...?X-Amz-Algorithm=...&X-Amz-Credential=AKIA.../20261124/...&X-Amz-Signature=abc...
    [GeneratedRegex(
        @"(?<name>X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token)=[^&\s""']*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PresignedQuery();
}
