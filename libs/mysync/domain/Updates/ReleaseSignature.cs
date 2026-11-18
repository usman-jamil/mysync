using System.Security.Cryptography;

namespace MySync.Domain.Updates;

/// <summary>
/// Checks that <c>SHA256SUMS.txt</c> was signed with the release key (ECDSA P-256 over SHA-256,
/// DER signature, as <c>openssl dgst -sha256 -sign</c> writes it). The checksums alone only prove a
/// download wasn't corrupted; the signature proves the release came from whoever holds the key, so
/// someone who can change a GitHub release can't hand mysync a binary of their own.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// The public half of the release signing key. The private half is the repository secret
    /// <c>MYSYNC_RELEASE_SIGNING_KEY</c>; the release workflow signs with it and checks against
    /// <c>tools/release/mysync-release-signing.pub.pem</c>, which a test keeps identical to this.
    /// Rotating the key means every installed copy before the rotation can't verify new releases.
    /// </summary>
    public const string ProductionPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXY20aDASj1RWM+Vq4jdThyocT2Ge
        mY1VkAKYECLHhDwSH413cffvkt9T14jYf8QX8FJIM/+WCXjHdtV/wudqEg==
        -----END PUBLIC KEY-----
        """;

    /// <summary>True only if <paramref name="signature"/> is a valid signature of <paramref name="checksums"/> under <paramref name="publicKeyPem"/>.</summary>
    public static bool IsValid(ReadOnlySpan<byte> checksums, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(checksums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false; // a malformed signature or key never verifies
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
