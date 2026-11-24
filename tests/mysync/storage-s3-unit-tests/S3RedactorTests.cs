using Shouldly;
using Xunit;

namespace MySync.Storage.S3.UnitTests;

/// <summary>No AWS secret survives into a log line: keys, session tokens, and a presigned URL's signature, credential and token.</summary>
public class S3RedactorTests
{
    [Theory]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "AWS_SECRET_ACCESS_KEY=***")]
    [InlineData("export AWS_SESSION_TOKEN='IQoJb3JpZ2luX2VjEXAMPLE//////////wEaCXVz'", "export AWS_SESSION_TOKEN='***'")]
    [InlineData("aws_secret_access_key = wJalrXUtnFEMI/K7MDENG", "aws_secret_access_key = ***")]
    [InlineData("{\"SecretAccessKey\":\"abc/def+ghi\",\"SessionToken\":\"tok\"}", "{\"SecretAccessKey\":\"***\",\"SessionToken\":\"***\"}")]
    public void NamedSecrets_Should_BeMasked(string text, string expected) =>
        S3Redactor.Redact(text).ShouldBe(expected);

    [Fact]
    public void APresignedUrl_Should_KeepItsPath_ButLoseItsSignatureCredentialAndToken()
    {
        const string Url =
            "https://team-shares.s3.eu-west-1.amazonaws.com/shares/x/objects/ab/abcd?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20261124%2Feu-west-1%2Fs3%2Faws4_request&X-Amz-Date=20261124T101500Z" +
            "&X-Amz-Expires=3600&X-Amz-Security-Token=IQoJb3JpZ2luEXAMPLE&X-Amz-SignedHeaders=host&X-Amz-Signature=fe5f80f77d5fa3beca038a248ff027";

        string redacted = S3Redactor.Redact(Url);

        redacted.ShouldBe(
            "https://team-shares.s3.eu-west-1.amazonaws.com/shares/x/objects/ab/abcd?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=***&X-Amz-Date=20261124T101500Z&X-Amz-Expires=3600&X-Amz-Security-Token=***&X-Amz-SignedHeaders=host&X-Amz-Signature=***");
        redacted.ShouldNotContain("AKIA");
    }

    [Fact]
    public void TextWithoutSecrets_Should_BeLeftAlone()
    {
        S3Redactor.Redact("NoSuchKey: the key shares/x/manifest.json does not exist").ShouldBe("NoSuchKey: the key shares/x/manifest.json does not exist");
        S3Redactor.Redact(null).ShouldBeEmpty();
    }
}
