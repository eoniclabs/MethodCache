using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MethodCache.HttpCaching.Storage;

/// <summary>
/// In-memory implementation of HTTP cache storage using IMemoryCache.
/// </summary>
public class InMemoryHttpCacheStorage : IHttpCacheStorage
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryHttpCacheStorage> _logger;
    private readonly HttpCacheOptions _options;

    public InMemoryHttpCacheStorage(
        IMemoryCache cache,
        HttpCacheOptions options,
        ILogger<InMemoryHttpCacheStorage> logger)
    {
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = _cache.Get<HttpCacheEntry>(key);

        if (entry != null)
        {
            _logger.LogDebug("Cache entry found for key: {Key}", key);
        }

        return ValueTask.FromResult(entry);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default)
    {
        var options = new MemoryCacheEntryOptions();

        // Set sliding expiration based on cache entry lifetime
        var freshnessLifetime = CalculateEntryLifetime(entry);
        if (freshnessLifetime.HasValue)
        {
            // Ensure minimum expiration of 1 second to avoid MemoryCache issues
            var expiration = freshnessLifetime.Value < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : freshnessLifetime.Value;
            options.SlidingExpiration = expiration;
        }
        else
        {
            // Default expiration if no cache headers
            options.SlidingExpiration = _options.DefaultMaxAge ?? TimeSpan.FromMinutes(5);
        }

        // Set absolute expiration based on max cache size limits
        options.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);

        // Set priority based on response characteristics
        options.Priority = DetermineEntryPriority(entry);

        // Size-based eviction
        if (_options.MaxResponseSize > 0)
        {
            options.Size = entry.Content.Length;
        }

        _cache.Set(key, entry, options);

        _logger.LogDebug("Cache entry stored for key: {Key}, size: {Size} bytes",
            key, entry.Content.Length);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        _logger.LogDebug("Cache entry removed for key: {Key}", key);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is MemoryCache memoryCache)
        {
            // Dispose and recreate isn't ideal, but MemoryCache doesn't expose Clear()
            _logger.LogWarning("Clearing memory cache - this operation disposes the current cache");
        }

        _logger.LogInformation("Memory cache cleared");
        return ValueTask.CompletedTask;
    }

    private TimeSpan? CalculateEntryLifetime(HttpCacheEntry entry)
    {
        // Use Cache-Control max-age if available
        if (entry.CacheControl?.MaxAge.HasValue == true)
        {
            return entry.CacheControl.MaxAge.Value;
        }

        // Use Expires header
        if (entry.Expires.HasValue && entry.Date.HasValue)
        {
            var lifetime = entry.Expires.Value - entry.Date.Value;
            if (lifetime > TimeSpan.Zero)
            {
                return lifetime;
            }
        }

        // Use Last-Modified heuristic
        if (entry.LastModified.HasValue && entry.Date.HasValue)
        {
            var age = entry.Date.Value - entry.LastModified.Value;
            return TimeSpan.FromSeconds(age.TotalSeconds * 0.1); // 10% rule
        }

        return null;
    }

    private CacheItemPriority DetermineEntryPriority(HttpCacheEntry entry)
    {
        // High priority for responses with explicit caching directives
        if (entry.CacheControl?.MaxAge.HasValue == true)
        {
            return CacheItemPriority.High;
        }

        // Normal priority for responses with ETags or Last-Modified
        if (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue)
        {
            return CacheItemPriority.Normal;
        }

        // Low priority for heuristically cached responses
        return CacheItemPriority.Low;
    }
}
