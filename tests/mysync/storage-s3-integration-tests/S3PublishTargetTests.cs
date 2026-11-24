using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Content;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Storage.S3.IntegrationTests;

/// <summary>
/// <see cref="S3PublishTarget"/> against a real, public, test-only publishing bucket (bucket policy:
/// anonymous s3:GetObject on p/* and robots.txt only), read anonymously through its HTTPS address.
/// This is the only place the anonymous probe and the listing check meet real S3.
/// </summary>
public sealed class S3PublishTargetTests(S3StorageFixture s3)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Prefix() => $"p/{PublishId.New()}/";

#pragma warning disable CA2000 // The target lives for the test.
    private S3PublishTarget Target(S3TransferSettings? settings = null)
    {
        string bucket = s3.Storage.PublishBucket;
        string region = s3.Storage.Region;
        S3Connection connection = new(
            () => S3Bucket.From(bucket, region, string.Empty, S3PublishOptions.BucketVariable, S3PublishOptions.RegionVariable),
            profile: null,
            settings ?? S3Stores.TestSettings,
            logger: null);
        return new S3PublishTarget(connection, Options.Create(new S3PublishOptions { BaseUrl = s3.Storage.PublishBaseUrl }), http: null, TimeProvider.System, logger: null, ownsConnection: true);
    }
#pragma warning restore CA2000

    [Fact]
    public async Task Preflight_Should_Pass_OnTheDocumentedBucketPolicy()
    {
        using S3PublishTarget target = Target();

        Result ready = await target.PreflightAsync(Ct);

        ready.IsSuccess.ShouldBeTrue(ready.IsFailure ? ready.Error.Description : null);
        List<S3Object> probes = await s3.Storage.ListAsync(S3PublishTarget.ProbePrefix, s3.Storage.PublishBucket);
        probes.ShouldBeEmpty("the probe is removed");
    }

    [Fact]
    public async Task APublishedFile_Should_BeServedAnonymously_WithItsHeaders_AndAMissingOneNotServed()
    {
        using S3PublishTarget target = Target();
        string prefix = Prefix();
        byte[] gzip = Gzip(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 500).Select(i => $"line {i}\n"))));

        (await target.PutAsync(prefix + "files/app.log", new MemoryStream(gzip), new PublishedBlob("text/plain; charset=utf-8", "inline", PublishedBlob.Immutable, "gzip"), onlyIfAbsent: false, Ct))
            .Value.ShouldBeTrue();

        Uri root = new(s3.Storage.PublishBaseUrl + "/");
        using HttpClientHandler raw = new() { AutomaticDecompression = DecompressionMethods.None, CheckCertificateRevocationList = true };
        using HttpClient anonymous = new(raw);
        using HttpResponseMessage served = await anonymous.GetAsync(new Uri(root, prefix + "files/app.log"), Ct);
        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
        served.Content.Headers.ContentType!.ToString().ShouldBe("text/plain; charset=utf-8");
        served.Content.Headers.ContentDisposition!.ToString().ShouldBe("inline");
        served.Headers.CacheControl!.ToString().ShouldBe("public, max-age=31536000, immutable");
        (await served.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(gzip);

        using HttpResponseMessage missing = await anonymous.GetAsync(new Uri(root, prefix + "files/no-such-file"), Ct);
        target.GetLocation().Value.NotServedStatusCodes.ShouldContain((int)missing.StatusCode);

        (await target.DeletePrefixAsync(prefix, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ANativeCopy_Should_ReplaceTheMetadata_WithThePublicationsHeaders()
    {
        using S3PublishTarget target = Target();
        (await target.PreflightAsync(Ct)).IsSuccess.ShouldBeTrue();
        (CopySource source, byte[] content) = await StoredObjectAsync(1024);
        target.CanCopy(source).ShouldBeTrue("same credentials, same provider");
        string name = Prefix() + "files/report.pdf";

        (await target.CopyAsync(source, content.Length, name, new PublishedBlob("application/pdf", "inline; filename*=UTF-8''report.pdf", PublishedBlob.Immutable), Ct))
            .Value.ShouldBe(CopyMethod.CopyObject);

        using AmazonS3Client client = s3.Storage.Client();
        GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(s3.Storage.PublishBucket, name, Ct);
        head.Headers.ContentType.ShouldBe("application/pdf");
        head.Headers.ContentDisposition.ShouldBe("inline; filename*=UTF-8''report.pdf");
        head.Headers.CacheControl.ShouldBe(PublishedBlob.Immutable);
        head.Metadata.Keys.ShouldNotContain("x-amz-meta-sha256", "REPLACE drops the share object's metadata");
    }

    [Fact]
    public async Task ACopyAboveTheThreshold_Should_BeAMultipartCopy_ByteForByte()
    {
        // The real switch is at 5 GiB; lowered to 5 MiB here, so an 11 MiB object is copied in three parts.
        using S3PublishTarget target = Target(S3Stores.TestSettings with { CopyObjectMaxBytes = S3Limits.MinPartSize, CopyPartSize = S3Limits.MinPartSize });
        (await target.PreflightAsync(Ct)).IsSuccess.ShouldBeTrue();
        (CopySource source, byte[] content) = await StoredObjectAsync(11 * 1024 * 1024 + 9);
        string name = Prefix() + "files/big.bin";

        (await target.CopyAsync(source, content.Length, name, new PublishedBlob("application/octet-stream", "attachment"), Ct)).Value.ShouldBe(CopyMethod.MultipartCopy);

        using AmazonS3Client client = s3.Storage.Client();
        using GetObjectResponse copied = await client.GetObjectAsync(s3.Storage.PublishBucket, name, Ct);
        copied.Headers.ContentDisposition.ShouldBe("attachment");
        using MemoryStream bytes = new();
        await copied.ResponseStream.CopyToAsync(bytes, Ct);
        bytes.ToArray().ShouldBe(content);
    }

    [Fact]
    public async Task DeletePrefix_Should_RemoveOnlyThatPublication()
    {
        using S3PublishTarget target = Target();
        string mine = Prefix();
        string theirs = Prefix();
        foreach (string name in (string[])[mine + "index.html", mine + "files/a.txt", mine + "files/b/c.txt", theirs + "index.html"])
        {
            (await target.PutAsync(name, new MemoryStream("x"u8.ToArray()), new PublishedBlob("text/plain"), false, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await target.DeletePrefixAsync(mine, Ct)).Value.ShouldBe(3);

        (await s3.Storage.ListAsync(mine, s3.Storage.PublishBucket)).ShouldBeEmpty();
        (await s3.Storage.ListAsync(theirs, s3.Storage.PublishBucket)).Select(item => item.Key).ShouldBe([theirs + "index.html"]);
        (await target.DeletePrefixAsync(theirs, Ct)).Value.ShouldBe(1);
    }

    [Fact]
    public async Task PutOnlyIfAbsent_Should_LeaveAnExistingObjectAlone()
    {
        using S3PublishTarget target = Target();
        string name = Prefix() + "assets/app.js";

        (await target.PutAsync(name, new MemoryStream("first"u8.ToArray()), new PublishedBlob("text/javascript; charset=utf-8"), onlyIfAbsent: true, Ct)).Value.ShouldBeTrue();
        (await target.PutAsync(name, new MemoryStream("second"u8.ToArray()), new PublishedBlob("text/javascript; charset=utf-8"), onlyIfAbsent: true, Ct)).Value.ShouldBeFalse();
    }

    private async Task<(CopySource Source, byte[] Content)> StoredObjectAsync(int size)
    {
        S3ShareStore store = S3Stores.ShareStore(s3.Storage);
        byte[] content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(size);
        var shareId = ShareId.New();
        ContentHash hash = ManifestBuilder.HashOf(content);
        (await store.UploadObjectAsync(shareId, hash, ObjectUpload.Raw(new MemoryStream(content), content.Length), Ct)).IsSuccess.ShouldBeTrue();
        return ((await store.GetCopySourceAsync(shareId, hash, TimeSpan.FromMinutes(5), Ct)).Value!, content);
    }

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.GZipStream gzip = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }
}
