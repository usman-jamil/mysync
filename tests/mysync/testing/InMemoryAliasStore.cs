using System.Globalization;
using System.Runtime.CompilerServices;
using MySync.Application.Abstractions.Storage;
using MySync.Domain.Shares;
using MySync.Domain.Storage;
using SharedKernel;

namespace MySync.Testing;

/// <summary>
/// An <see cref="IAliasStore"/> in memory, with the same conditional-write semantics as Azure
/// (<c>If-None-Match: *</c> on create, <c>If-Match</c> on delete) and the same blob names
/// (<see cref="ShareLayout.Alias"/>). Pinned by <see cref="Contracts.AliasStoreContractTests"/>,
/// which also runs against real Azure and S3 storage. Thread-safe.
/// </summary>
public sealed class InMemoryAliasStore : IAliasStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, Entry> _blobs = new(StringComparer.Ordinal);
    private long _etagCounter;

    /// <summary>Runs before each create is checked: lets a test claim the name as "another machine" first.</summary>
    public Func<ShareName, Task>? BeforeCreate { get; set; }

    /// <summary>Runs before each create; return an error to make it fail.</summary>
    public Func<ShareName, Error?>? FailCreate { get; set; }

    /// <summary>Runs after <see cref="FindByShareAsync"/> has listed, before its caller reads each name again.</summary>
    public Action<IReadOnlyList<AliasRecord>>? AfterFindByShare { get; set; }

    /// <summary>Runs before each delete is checked: lets a test re-point the name as "another machine" first.</summary>
    public Action<ShareName>? BeforeDelete { get; set; }

    /// <summary>Every blob name under <c>aliases/</c>, sorted: what a test can see in the "container".</summary>
    public IReadOnlyList<string> BlobNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _blobs.Keys];
            }
        }
    }

    /// <summary>The order things happened in: <c>create:&lt;name&gt;</c>, <c>delete:&lt;name&gt;</c>.</summary>
    public List<string> Operations { get; } = [];

    public async Task<Result<string>> CreateAsync(AliasRecord alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alias);
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeCreate is { } hook)
        {
            await hook(alias.Name);
        }

        if (FailCreate?.Invoke(alias.Name) is { } failure)
        {
            return Result.Failure<string>(failure);
        }

        string name = ShareLayout.Alias(alias.Name);
        lock (_gate)
        {
            if (_blobs.ContainsKey(name))
            {
                return Result.Failure<string>(StorageErrors.AlreadyExists(name));
            }

            Operations.Add($"create:{alias.Name}");
            return Result.Success(Put(name, alias));
        }
    }

    public Task<Result<AliasSnapshot?>> GetAsync(ShareName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_blobs.TryGetValue(ShareLayout.Alias(name), out Entry? entry))
            {
                return Task.FromResult(Result.Success<AliasSnapshot?>(null));
            }

            return Task.FromResult(entry.Alias is { } alias
                ? Result.Success<AliasSnapshot?>(new AliasSnapshot(alias, entry.ETag))
                : Result.Failure<AliasSnapshot?>(StorageErrors.InvalidAlias(name.Value, "its share ID is missing or malformed")));
        }
    }

    public async IAsyncEnumerable<Result<AliasRecord>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<(string Name, Entry Entry)> snapshot;
        lock (_gate)
        {
            snapshot = [.. _blobs.Select(pair => (pair.Key, pair.Value))];
        }

        foreach ((string blobName, Entry entry) in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (!ShareLayout.TryParseAlias(blobName, out ShareName? name))
            {
                yield return Result.Failure<AliasRecord>(StorageErrors.StrayAlias(blobName));
            }
            else
            {
                yield return entry.Alias ?? Result.Failure<AliasRecord>(StorageErrors.InvalidAlias(name.Value, "its share ID is missing or malformed"));
            }
        }
    }

    public async Task<Result<IReadOnlyList<AliasRecord>>> FindByShareAsync(ShareId shareId, CancellationToken cancellationToken)
    {
        List<AliasRecord> found = [];
        await foreach (Result<AliasRecord> alias in ListAsync(cancellationToken))
        {
            if (alias.IsSuccess && alias.Value.ShareId == shareId)
            {
                found.Add(alias.Value);
            }
        }

        AfterFindByShare?.Invoke(found);
        return found;
    }

    public Task<Result> DeleteAsync(ShareName name, string ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        string blobName = ShareLayout.Alias(name);
        BeforeDelete?.Invoke(name);

        lock (_gate)
        {
            if (!_blobs.TryGetValue(blobName, out Entry? entry) || !string.Equals(entry.ETag, ifMatchETag, StringComparison.Ordinal))
            {
                return Task.FromResult(Result.Failure(StorageErrors.PreconditionFailed(blobName)));
            }

            _blobs.Remove(blobName);
            Operations.Add($"delete:{name}");
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Points an existing name at another share, as another machine's delete-and-reclaim would.</summary>
    public void Repoint(ShareName name, ShareId shareId)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            string blobName = ShareLayout.Alias(name);
            AliasRecord current = _blobs[blobName].Alias!;
            Put(blobName, current with { ShareId = shareId });
        }
    }

    /// <summary>An alias blob whose metadata can't be read, as a damaged or hand-made one would be.</summary>
    public void PutMalformed(string name)
    {
        lock (_gate)
        {
            Put(ShareLayout.AliasesPrefix + name, alias: null);
        }
    }

    /// <summary>A blob under <c>aliases/</c> that isn't a share name.</summary>
    public void PutStray(string blobName)
    {
        lock (_gate)
        {
            Put(blobName, alias: null);
        }
    }

    private string Put(string blobName, AliasRecord? alias)
    {
        string etag = string.Create(CultureInfo.InvariantCulture, $"\"0x{++_etagCounter:X}\"");
        _blobs[blobName] = new Entry(alias, etag);
        return etag;
    }

    private sealed record Entry(AliasRecord? Alias, string ETag);
}
