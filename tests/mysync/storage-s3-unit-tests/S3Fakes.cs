using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Storage;
using MySync.Infrastructure;
using NSubstitute;
using SharedKernel;

namespace MySync.Storage.S3.UnitTests;

/// <summary>A substitute <see cref="IAmazonS3"/> behind a real <see cref="S3Connection"/>, and S3's error answers.</summary>
internal static class S3Fakes
{
    public const string Bucket = "team-shares";
    public const string Region = "eu-west-1";
    public const string Scope = "aws:0123456789abcdef";

    private static readonly ServiceProvider Infrastructure = new ServiceCollection().AddInfrastructure().BuildServiceProvider();

    public static IManifestSerializer Serializer { get; } = Infrastructure.GetRequiredService<IManifestSerializer>();

    public static IPublicationRegistrySerializer RegistrySerializer { get; } = Infrastructure.GetRequiredService<IPublicationRegistrySerializer>();

    /// <summary>S3's minimum is 5 MiB, but a substitute takes any size: small parts make multipart uploads cheap to test.</summary>
    public static S3TransferSettings Small(long partSize = 1024, int conflictAttempts = 3) =>
        new(partSize, MaxRetries: 0, TimeSpan.FromSeconds(5), conflictAttempts, TimeSpan.FromMilliseconds(1));

    public static S3Bucket TheBucket(string prefix = "") => S3Bucket.From(Bucket, Region, prefix, S3StorageOptions.BucketVariable, S3StorageOptions.RegionVariable).Value;

    public static S3Connection Connection(IAmazonS3 api, S3TransferSettings? settings = null, string prefix = "") =>
        new(() => TheBucket(prefix), (_, _) => Task.FromResult(Result.Success(new S3Client(api, Scope))), settings ?? Small());

#pragma warning disable CA2000 // The connection only holds the substitute; nothing to dispose in a test.
    public static S3ShareStore ShareStore(IAmazonS3 api, S3TransferSettings? settings = null) =>
        new(Connection(api, settings), Serializer, RegistrySerializer, TimeProvider.System, options: null, logger: null);
#pragma warning restore CA2000

    public static AmazonS3Exception Error(HttpStatusCode status, string code, string? body = null)
    {
        AmazonS3Exception exception = new($"{code} (test)", Amazon.Runtime.ErrorType.Sender, code, "request-1", status);
        if (body is not null)
        {
            typeof(AmazonS3Exception).GetProperty(nameof(AmazonS3Exception.ResponseBody))!.SetValue(exception, body);
        }

        return exception;
    }

    public static AmazonS3Exception PreconditionFailed() => Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");

    public static AmazonS3Exception ConditionalConflict() => Error(HttpStatusCode.Conflict, "ConditionalRequestConflict");

    public static AmazonS3Exception NoSuchKey() => Error(HttpStatusCode.NotFound, "NoSuchKey");

    public static AmazonS3Exception HeadNotFound() => Error(HttpStatusCode.NotFound, "NotFound");

    public static async Task<byte[]> ReadAll(Stream stream)
    {
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}

/// <summary>Remembers the bytes each request carried (the SDK's request streams are read once).</summary>
internal sealed class Captured
{
    public ConcurrentQueue<(string Operation, string Key, byte[] Bytes)> Calls { get; } = new();

    public IReadOnlyList<string> Operations => [.. Calls.Select(call => call.Operation)];

    public void Add(string operation, string key, Stream? body = null)
    {
        byte[] bytes = [];
        if (body is not null)
        {
            using MemoryStream copy = new();
            body.CopyTo(copy);
            bytes = copy.ToArray();
        }

        Calls.Enqueue((operation, key, bytes));
    }
}
