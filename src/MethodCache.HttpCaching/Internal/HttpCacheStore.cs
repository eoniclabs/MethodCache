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
