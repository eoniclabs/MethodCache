using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// The built-in HTTP cache storage: a size-limited memory cache of its own (it never touches the application's
/// <c>IMemoryCache</c>), plus the registered <see cref="IDistributedCache"/> when
/// <see cref="HttpCacheStorageOptions.UseDistributedCache"/> is on, so every instance of the app shares responses.
/// </summary>
internal sealed class HttpCacheStore : IHttpCacheStorage, IDisposable
{
    private const string DistributedKeyPrefix = "methodcache-http:";

    private readonly MemoryCache _memory;
    private readonly IDistributedCache? _distributed;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpCacheStore> _logger;
    private readonly long _maxCacheSize;

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
        _memory = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _maxCacheSize > 0 ? _maxCacheSize : null
        });
    }

    public async ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_memory.TryGetValue(key, out HttpCacheEntry? entry))
        {
            return entry;
        }

        if (_distributed == null)
        {
            return null;
        }

        try
        {
            var data = await _distributed.GetAsync(DistributedKeyPrefix + key, cancellationToken).ConfigureAwait(false);
            if (data == null || HttpCacheEntrySerializer.Deserialize(data) is not { } stored)
            {
                return null;
            }

            var remaining = stored.ExpiresAt - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            SetLocal(key, stored.Entry, remaining);
            return stored.Entry;
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

        SetLocal(key, entry, timeToLive);

        if (_distributed == null)
        {
            return;
        }

        try
        {
            var data = HttpCacheEntrySerializer.Serialize(entry, _timeProvider.GetUtcNow() + timeToLive);
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive };
            await _distributed.SetAsync(DistributedKeyPrefix + key, data, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Writing the HTTP cache entry {Key} to the distributed cache failed", key);
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _memory.Remove(key);

        if (_distributed == null)
        {
            return;
        }

        try
        {
            await _distributed.RemoveAsync(DistributedKeyPrefix + key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Removing the HTTP cache entry {Key} from the distributed cache failed", key);
        }
    }

    /// <summary>
    /// Clears this instance's memory. <see cref="IDistributedCache"/> cannot enumerate keys, so distributed entries
    /// stay until they expire.
    /// </summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
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
}
