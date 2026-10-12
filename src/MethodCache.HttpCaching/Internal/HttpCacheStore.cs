using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// The built-in HTTP cache storage: a size-limited memory cache of its own (it never touches the application's
/// <c>IMemoryCache</c>), or the registered <see cref="IDistributedCache"/> when
/// <see cref="HttpCacheStorageOptions.UseDistributedCache"/> is on, so every instance of the app shares responses.
/// Distributed mode keeps no local copies: an invalidation after a POST on one instance must be seen by all of them.
/// </summary>
internal sealed class HttpCacheStore : IHttpCacheStorage, IDisposable
{
    // A fixed number of non-expiring epoch slots bounds metadata without allowing an expired tombstone to
    // resurrect an old response. A collision only causes an additional cache miss for an unrelated URL.
    private const int GenerationSlots = 4096;
    // Maximum absolute expiry also overrides providers' default sliding expiry (such as SQL Server).
    private static DistributedCacheEntryOptions PermanentEntry => new() { AbsoluteExpiration = DateTimeOffset.MaxValue };
    private readonly string?[] _generations = new string?[GenerationSlots];
    private readonly MemoryCache _memory;
    private readonly IDistributedCache? _distributed;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpCacheStore> _logger;
    private readonly long _maxCacheSize;
    private readonly string _keyPrefix;

    public HttpCacheStore(
        IOptions<HttpCacheStorageOptions> options,
        ILogger<HttpCacheStore> logger,
        TimeProvider? timeProvider = null,
        IDistributedCache? distributed = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _distributed = distributed;
        _maxCacheSize = options.Value.MaxCacheSize;
        _keyPrefix = options.Value.KeyPrefix;
        _memory = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _maxCacheSize > 0 ? _maxCacheSize : null,
            // Entries expire on the same clock the handler judges freshness by
            Clock = ReferenceEquals(_timeProvider, TimeProvider.System) ? null : new TimeProviderClock(_timeProvider)
        });
    }

    public async ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = GenerationSlot(key);
        if (_distributed == null)
        {
            // Process-local slots are never evicted and live exactly as long as the entries they guard, so the
            // empty initial namespace cannot come back after an invalidation.
            return Volatile.Read(ref _generations[slot]) ?? string.Empty;
        }

        try
        {
            var generationKey = GenerationKey(slot);
            var bytes = await _distributed.GetAsync(generationKey, cancellationToken).ConfigureAwait(false);
            if (bytes == null)
            {
                // A missing record (never written, or evicted by the backend) must never map to a namespace that
                // was used before: start a fresh one. Entries of the lost generation become unreachable, so a lost
                // record can only cause misses. The new value is unique, so even if this write lands after a
                // concurrent invalidation it cannot make a previously observed namespace current again.
                var fresh = Guid.NewGuid();
                await _distributed.SetAsync(generationKey, fresh.ToByteArray(), PermanentEntry, cancellationToken).ConfigureAwait(false);
                return fresh.ToString("N");
            }
            return bytes.Length == 16 ? new Guid(bytes).ToString("N") : Guid.NewGuid().ToString("N");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading an HTTP cache generation failed");
            return Guid.NewGuid().ToString("N"); // Fail open to the origin, never to an old cache namespace.
        }
    }

    public async ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = GenerationSlot(key);
        if (_distributed == null)
        {
            Volatile.Write(ref _generations[slot], Guid.NewGuid().ToString("N"));
            return;
        }

        var generationKey = GenerationKey(slot);
        // The namespace to retire is the one current now, after the origin write. Every generation value is
        // unique and written once, so any stored value different from it was written after this read: ours, or a
        // concurrent invalidator's (another instance, or a URL sharing the slot). Either retires the old namespace.
        var previous = await _distributed.GetAsync(generationKey, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generation = Guid.NewGuid().ToByteArray();
            await _distributed.SetAsync(generationKey, generation, PermanentEntry, cancellationToken).ConfigureAwait(false);
            var stored = await _distributed.GetAsync(generationKey, cancellationToken).ConfigureAwait(false);
            if (stored != null && (previous == null || !previous.AsSpan().SequenceEqual(stored)))
            {
                return;
            }
            // Some providers ignore an insert lost to a concurrent first insert. Retry with a fresh generation;
            // never retry a previously observed value, which could make old response data reachable again.
        }
        throw new InvalidOperationException("The distributed HTTP cache could not confirm the invalidation generation.");
    }

    private string GenerationKey(int slot) => _keyPrefix + "$generation:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int GenerationSlot(string key)
    {
        var hash = 2166136261u;
        foreach (var character in key)
        {
            hash = unchecked((hash ^ character) * 16777619u);
        }
        return (int)(hash & (GenerationSlots - 1));
    }

    public async ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_distributed == null)
        {
            return _memory.TryGetValue(key, out HttpCacheEntry? entry) ? entry : null;
        }

        try
        {
            var data = await _distributed.GetAsync(_keyPrefix + key, cancellationToken).ConfigureAwait(false);
            if (data == null || HttpCacheEntrySerializer.Deserialize(data) is not { } stored)
            {
                return null;
            }

            return stored.ExpiresAt > _timeProvider.GetUtcNow() ? stored.Entry : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the HTTP cache entry {Key} from the distributed cache failed", key);
            return null;
        }
    }

    public async ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            return;
        }

        if (_distributed == null)
        {
            SetLocal(key, entry, timeToLive);
            return;
        }

        try
        {
            var data = HttpCacheEntrySerializer.Serialize(entry, _timeProvider.GetUtcNow() + timeToLive);
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive };
            await _distributed.SetAsync(_keyPrefix + key, data, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Writing the HTTP cache entry {Key} to the distributed cache failed", key);
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_distributed == null)
        {
            _memory.Remove(key);
            return;
        }

        try
        {
            await _distributed.RemoveAsync(_keyPrefix + key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Removing the HTTP cache entry {Key} from the distributed cache failed", key);
        }
    }

    /// <summary>
    /// Clears the memory cache. <see cref="IDistributedCache"/> cannot enumerate keys, so distributed entries stay
    /// until they expire.
    /// </summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_distributed != null)
        {
            _logger.LogWarning("ClearAsync cannot clear the distributed HTTP cache; its entries stay until they expire");
            return ValueTask.CompletedTask;
        }

        for (var slot = 0; slot < GenerationSlots; slot++)
        {
            Volatile.Write(ref _generations[slot], Guid.NewGuid().ToString("N"));
        }
        _memory.Clear();
        _logger.LogInformation("HTTP cache cleared");
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _memory.Dispose();

    private void SetLocal(string key, HttpCacheEntry entry, TimeSpan timeToLive)
    {
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive };
        if (_maxCacheSize > 0)
        {
            options.Size = Math.Max(1, entry.Content.Length);
        }

        _memory.Set(key, entry, options);
    }

    private sealed class TimeProviderClock(TimeProvider timeProvider) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}
