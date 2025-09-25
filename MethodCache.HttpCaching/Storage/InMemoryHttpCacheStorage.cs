using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Storage;

/// <summary>
/// In-memory implementation of HTTP cache storage using IMemoryCache.
/// </summary>
public class InMemoryHttpCacheStorage : IHttpCacheStorage
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryHttpCacheStorage> _logger;
    private readonly CacheBehaviorOptions _behavior;
    private readonly CacheFreshnessOptions _freshness;
    private readonly HttpCacheStorageOptions _storage;

    public InMemoryHttpCacheStorage(
        IMemoryCache cache,
        IOptions<HttpCacheOptions> cacheOptions,
        IOptions<HttpCacheStorageOptions> storageOptions,
        ILogger<InMemoryHttpCacheStorage> logger)
    {
        _cache = cache;
        _logger = logger;
        var options = cacheOptions.Value;
        _behavior = options.Behavior;
        _freshness = options.Freshness;
        _storage = storageOptions.Value;
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

    public ValueTask SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default)
    {
        if (_storage.MaxResponseSize > 0 && entry.Content.Length > _storage.MaxResponseSize)
        {
            _logger.LogDebug("Entry {Key} exceeds in-memory response size limit ({Size} > {Limit}), skipping", key, entry.Content.Length, _storage.MaxResponseSize);
            return ValueTask.CompletedTask;
        }

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            Priority = DetermineEntryPriority(entry)
        };

        var lifetime = CalculateEntryLifetime(entry) ?? _freshness.DefaultMaxAge ?? TimeSpan.FromMinutes(5);
        if (lifetime < _freshness.MinExpiration)
        {
            lifetime = _freshness.MinExpiration;
        }

        cacheEntryOptions.AbsoluteExpirationRelativeToNow = lifetime;

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
        if (_cache is MemoryCache memoryCache)
        {
            memoryCache.Compact(1.0);
        }

        _logger.LogInformation("Memory cache cleared");
        return ValueTask.CompletedTask;
    }

    private TimeSpan? CalculateEntryLifetime(HttpCacheEntry entry)
    {
        if (_behavior.RespectCacheControl)
        {
            if (_behavior.IsSharedCache && entry.CacheControl?.SharedMaxAge.HasValue == true)
            {
                return entry.CacheControl.SharedMaxAge.Value;
            }

            if (entry.CacheControl?.MaxAge.HasValue == true)
            {
                return entry.CacheControl.MaxAge.Value;
            }
        }

        if (entry.Expires.HasValue && entry.Date.HasValue)
        {
            var lifetime = entry.Expires.Value - entry.Date.Value;
            if (lifetime > TimeSpan.Zero)
            {
                return lifetime;
            }
        }

        if (_freshness.AllowHeuristicFreshness && entry.LastModified.HasValue && entry.Date.HasValue)
        {
            var age = entry.Date.Value - entry.LastModified.Value;
            var heuristic = TimeSpan.FromSeconds(Math.Max(0, age.TotalSeconds * 0.1));
            if (heuristic > _freshness.MaxHeuristicFreshness)
            {
                heuristic = _freshness.MaxHeuristicFreshness;
            }

            return heuristic;
        }

        return _freshness.DefaultMaxAge;
    }

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
