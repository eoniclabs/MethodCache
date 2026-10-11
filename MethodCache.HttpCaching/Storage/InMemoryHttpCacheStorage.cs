using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Storage;

/// <summary>
/// In-memory HTTP cache storage. It owns its own <see cref="MemoryCache"/>, so it never touches the application's
/// <see cref="IMemoryCache"/>.
/// </summary>
public sealed class InMemoryHttpCacheStorage : IHttpCacheStorage, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly ILogger<InMemoryHttpCacheStorage> _logger;
    private readonly HttpCacheStorageOptions _storage;

    public InMemoryHttpCacheStorage(
        IOptions<HttpCacheStorageOptions> storageOptions,
        ILogger<InMemoryHttpCacheStorage> logger)
    {
        _logger = logger;
        _storage = storageOptions.Value;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _storage.MaxCacheSize > 0 ? _storage.MaxCacheSize : null
        });
    }

    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = _cache.Get<HttpCacheEntry>(key);
        if (entry != null)
        {
            _logger.LogDebug("Cache entry found for key: {Key}", key);
        }

        return ValueTask.FromResult(entry);
    }

    public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        if (_storage.MaxResponseSize > 0 && entry.Content.Length > _storage.MaxResponseSize)
        {
            _logger.LogDebug("Entry {Key} exceeds in-memory response size limit ({Size} > {Limit}), skipping", key, entry.Content.Length, _storage.MaxResponseSize);
            return ValueTask.CompletedTask;
        }

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            Priority = DetermineEntryPriority(entry),
            AbsoluteExpirationRelativeToNow = timeToLive
        };
        if (_storage.MaxCacheSize > 0)
        {
            cacheEntryOptions.Size = Math.Max(1, entry.Content.Length);
        }

        _cache.Set(key, entry, cacheEntryOptions);
        _logger.LogDebug("Cache entry stored for key: {Key}, size: {Size} bytes", key, entry.Content.Length);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        _logger.LogDebug("Cache entry removed for key: {Key}", key);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        _cache.Clear();
        _logger.LogInformation("HTTP cache cleared");
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _cache.Dispose();

    private static CacheItemPriority DetermineEntryPriority(HttpCacheEntry entry)
    {
        if (entry.CacheControl?.MaxAge.HasValue == true)
        {
            return CacheItemPriority.High;
        }

        if (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue)
        {
            return CacheItemPriority.Normal;
        }

        return CacheItemPriority.Low;
    }
}
