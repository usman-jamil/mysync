using System.Security.Cryptography;
using System.Text;
using MySync.Domain.Updates;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Updates;

public class ReleaseSignatureTests
{
    private static readonly byte[] Sums = Encoding.UTF8.GetBytes("abc123  mysync-1.2.3-linux-x64.tar.gz\n");

    [Fact]
    public void ASignatureByTheKey_Should_Verify_AndAnythingElseNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = key.ExportSubjectPublicKeyInfoPem();
        byte[] signature = Sign(key, Sums);

        ReleaseSignature.IsValid(Sums, signature, publicKey).ShouldBeTrue();
        ReleaseSignature.IsValid(Encoding.UTF8.GetBytes("abc124  mysync-1.2.3-linux-x64.tar.gz\n"), signature, publicKey).ShouldBeFalse();
        ReleaseSignature.IsValid(Sums, Sign(other, Sums), publicKey).ShouldBeFalse();
        ReleaseSignature.IsValid(Sums, [1, 2, 3], publicKey).ShouldBeFalse();
        ReleaseSignature.IsValid(Sums, [], publicKey).ShouldBeFalse();
        ReleaseSignature.IsValid(Sums, signature, "-----BEGIN PUBLIC KEY-----\nnot a key\n-----END PUBLIC KEY-----").ShouldBeFalse();
    }

    [Fact]
    public void TheBuiltInKey_Should_BeTheOneTheReleaseWorkflowChecksAgainst()
    {
        string file = File.ReadAllText(Path.Combine(ReleaseAssetsTests.RepositoryRoot(), "tools", "release", "mysync-release-signing.pub.pem"));

        Normalize(ReleaseSignature.ProductionPublicKeyPem).ShouldBe(Normalize(file));
        using var key = ECDsa.Create();
        key.ImportFromPem(ReleaseSignature.ProductionPublicKeyPem);
        key.KeySize.ShouldBe(256);
    }

    // openssl dgst -sha256 -sign writes a DER signature; that's what the release carries.
    private static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static string Normalize(string pem) => pem.ReplaceLineEndings("\n").Trim();
}
