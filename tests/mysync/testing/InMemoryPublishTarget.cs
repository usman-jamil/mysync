using System.Collections.Concurrent;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Configuration;
using MySync.Domain.Publishing;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// A publishing account's static website in memory: blobs with their headers, the static website
/// setting, and every operation in order (so a test can pin "index.html first"). Server-side copies
/// read from <see cref="Source"/>'s URLs. Thread-safe.
/// </summary>
public sealed class InMemoryPublishTarget(InMemoryShareStore source, string accountName = "publishacct", string baseUrl = "https://publishacct.z13.web.core.windows.net") : IPublishTarget
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _operations = new();

    public InMemoryShareStore Source { get; } = source;

    /// <summary>What <see cref="GetLocation"/> reports instead of the location, e.g. a missing variable.</summary>
    public Error? LocationError { get; set; }

    public bool StaticWebsiteEnabled { get; set; } = true;

    public string? IndexDocument { get; set; } = "index.html";

    /// <summary>Simulates a credential that can't read service properties.</summary>
    public bool CannotReadServiceProperties { get; set; }

    /// <summary>Runs before each write or delete; return an error to make it fail.</summary>
    public Func<string, Error?>? FailOn { get; set; }

    /// <summary>Runs before each write; lets a test cancel or race at a chosen blob.</summary>
    public Action<string>? BeforeWrite { get; set; }

    /// <summary><c>put:&lt;name&gt;</c>, <c>copy:&lt;name&gt;</c>, <c>delete:&lt;name&gt;</c>, <c>delete-prefix:&lt;prefix&gt;</c>, in order.</summary>
    public IReadOnlyList<string> Operations => [.. _operations];

    public IReadOnlyCollection<string> BlobNames => [.. _blobs.Keys.Order(StringComparer.Ordinal)];

    public StoredBlob? Blob(string name) => _blobs.GetValueOrDefault(name);

    public Result<PublishLocation> GetLocation() =>
        LocationError is { } error ? Result.Failure<PublishLocation>(error) : new PublishLocation(accountName, new Uri(baseUrl));

    public Task<Result> CheckStaticWebsiteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CannotReadServiceProperties)
        {
            return Task.FromResult(Result.Failure(PublishErrors.CannotReadServiceProperties(accountName)));
        }

        if (!StaticWebsiteEnabled)
        {
            return Task.FromResult(Result.Failure(PublishErrors.StaticWebsiteDisabled(accountName)));
        }

        return Task.FromResult(IndexDocument == "index.html" ? Result.Success() : Result.Failure(PublishErrors.IndexDocumentMismatch(accountName, IndexDocument)));
    }

    public async Task<Result<bool>> PutAsync(string blobName, Stream content, PublishedBlob blob, bool onlyIfAbsent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (Before("put", blobName) is { } failure)
        {
            return Result.Failure<bool>(failure);
        }

        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken);
        StoredBlob stored = new(buffer.ToArray(), blob);
        if (onlyIfAbsent)
        {
            return Result.Success(_blobs.TryAdd(blobName, stored));
        }

        _blobs[blobName] = stored;
        return Result.Success(true);
    }

    public Task<Result<CopyMethod>> CopyAsync(Uri source, long length, string blobName, PublishedBlob blob, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Before("copy", blobName) is { } failure)
        {
            return Task.FromResult(Result.Failure<CopyMethod>(failure));
        }

        if (Source.ReadUrl(source) is not { } bytes)
        {
            return Task.FromResult(Result.Failure<CopyMethod>(StorageErrors.NotFound(source.ToString())));
        }

        _blobs[blobName] = new StoredBlob(bytes, blob);
        return Task.FromResult(Result.Success(CopyMethod.PutBlobFromUrl));
    }

    public Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Before("delete", blobName) is { } failure)
        {
            return Task.FromResult(Result.Failure(failure));
        }

        _blobs.TryRemove(blobName, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<Result<int>> DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        if (Before("delete-prefix", prefix) is { } failure)
        {
            return Task.FromResult(Result.Failure<int>(failure));
        }

        int count = 0;
        foreach (string name in _blobs.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (_blobs.TryRemove(name, out _))
            {
                count++;
            }
        }

        return Task.FromResult(Result.Success(count));
    }

    private Error? Before(string operation, string name)
    {
        _operations.Enqueue($"{operation}:{name}");
        if (operation is "put" or "copy")
        {
            BeforeWrite?.Invoke(name);
        }

        return FailOn?.Invoke(name);
    }

    public static Error MissingSettings => ConfigurationErrors.Missing(PublishErrors.PublishConnectionStringVariable);
}

/// <summary>A published blob: its bytes and the headers it's served with.</summary>
public sealed record StoredBlob(byte[] Bytes, PublishedBlob Headers);

/// <summary>
/// Sees a publication as the public would: through the target's blobs, at the URL's path. Set
/// <see cref="Unreachable"/> to simulate a wrong <c>MYSYNC_PUBLISH_BASE_URL</c>.
/// </summary>
public sealed class InMemoryPublicationVerifier(InMemoryPublishTarget target) : IPublicationVerifier
{
    public bool Unreachable { get; set; }

    public List<Uri> Requests { get; } = [];

    public Task<VerificationOutcome> VerifyAsync(Uri siteDataUrl, string publishId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(siteDataUrl);
        Requests.Add(siteDataUrl);
        if (Unreachable)
        {
            return Task.FromResult(new VerificationOutcome(false, "HTTP 404 Not Found"));
        }

        string name = Uri.UnescapeDataString(siteDataUrl.AbsolutePath.TrimStart('/'));
        bool found = target.Blob(name) is { } blob && System.Text.Encoding.UTF8.GetString(blob.Bytes).Contains($"\"{publishId}\"", StringComparison.Ordinal);
        return Task.FromResult(found ? new VerificationOutcome(true) : new VerificationOutcome(false, "HTTP 404 Not Found"));
    }
}
