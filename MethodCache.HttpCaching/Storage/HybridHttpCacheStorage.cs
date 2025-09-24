using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;

namespace MethodCache.HttpCaching.Storage;

/// <summary>
/// HTTP cache storage implementation that uses the hybrid storage infrastructure
/// to provide L1 (memory) + L2 (distributed) caching capabilities.
/// </summary>
public class HybridHttpCacheStorage : IHttpCacheStorage
{
    private readonly IStorageProvider _storageProvider;
    private readonly ILogger<HybridHttpCacheStorage> _logger;
    private readonly HttpCacheOptions _httpOptions;
    private readonly StorageOptions _storageOptions;

    // Statistics
    private long _requests;
    private long _hits;
    private long _misses;
    private long _sets;
    private long _removes;

    public HybridHttpCacheStorage(
        IStorageProvider storageProvider,
        IOptions<HttpCacheOptions> httpOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<HybridHttpCacheStorage> logger)
    {
        _storageProvider = storageProvider;
        _httpOptions = httpOptions.Value;
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    public async Task<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requests);

        try
        {
            var entry = await _storageProvider.GetAsync<HttpCacheEntry>(key, cancellationToken);

            if (entry != null)
            {
                Interlocked.Increment(ref _hits);
                _logger.LogDebug("HTTP cache hit for key {Key}", key);

                // Validate that the entry hasn't exceeded storage size limits
                if (IsEntrySizeValid(entry))
                {
                    return entry;
                }
                else
                {
                    _logger.LogWarning("HTTP cache entry {Key} exceeded size limits, removing from cache", key);
                    await RemoveAsync(key, cancellationToken);
                }
            }

            Interlocked.Increment(ref _misses);
            _logger.LogDebug("HTTP cache miss for key {Key}", key);
            return null;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _misses);
            _logger.LogError(ex, "Error retrieving HTTP cache entry for key {Key}", key);
            return null;
        }
    }

    public async Task SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default)
    {
        if (!IsEntrySizeValid(entry))
        {
            _logger.LogWarning("HTTP cache entry {Key} exceeds maximum response size ({Size} bytes), not caching",
                key, entry.Content.Length);
            return;
        }

        try
        {
            var expiration = CalculateExpiration(entry);
            var tags = GenerateTags(entry);

            await _storageProvider.SetAsync(key, entry, expiration, tags, cancellationToken);

            Interlocked.Increment(ref _sets);
            _logger.LogDebug("Stored HTTP cache entry {Key} with expiration {Expiration} and tags {Tags}",
                key, expiration, string.Join(", ", tags));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing HTTP cache entry for key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageProvider.RemoveAsync(key, cancellationToken);
            Interlocked.Increment(ref _removes);
            _logger.LogDebug("Removed HTTP cache entry {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing HTTP cache entry for key {Key}", key);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use tag-based invalidation to clear all HTTP cache entries
            await _storageProvider.RemoveByTagAsync("http-cache", cancellationToken);
            _logger.LogInformation("Cleared all HTTP cache entries using tag invalidation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing HTTP cache");
        }
    }

    /// <summary>
    /// Removes HTTP cache entries for a specific URI pattern.
    /// </summary>
    public async Task InvalidateByUriAsync(string uriPattern, CancellationToken cancellationToken = default)
    {
        try
        {
            var tag = $"uri:{uriPattern}";
            await _storageProvider.RemoveByTagAsync(tag, cancellationToken);
            _logger.LogDebug("Invalidated HTTP cache entries for URI pattern {UriPattern}", uriPattern);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating HTTP cache entries for URI pattern {UriPattern}", uriPattern);
        }
    }

    /// <summary>
    /// Removes HTTP cache entries for a specific HTTP method.
    /// </summary>
    public async Task InvalidateByMethodAsync(string method, CancellationToken cancellationToken = default)
    {
        try
        {
            var tag = $"method:{method.ToUpperInvariant()}";
            await _storageProvider.RemoveByTagAsync(tag, cancellationToken);
            _logger.LogDebug("Invalidated HTTP cache entries for method {Method}", method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating HTTP cache entries for method {Method}", method);
        }
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public HttpCacheStats GetStats()
    {
        var requests = Interlocked.Read(ref _requests);
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var sets = Interlocked.Read(ref _sets);
        var removes = Interlocked.Read(ref _removes);

        return new HttpCacheStats
        {
            Requests = requests,
            Hits = hits,
            Misses = misses,
            HitRatio = requests > 0 ? (double)hits / requests : 0.0,
            Sets = sets,
            Removes = removes,
            StorageProviderName = _storageProvider.Name
        };
    }

    private bool IsEntrySizeValid(HttpCacheEntry entry)
    {
        if (_httpOptions.MaxResponseSize <= 0)
            return true;

        var entrySize = entry.Content.Length;
        return entrySize <= _httpOptions.MaxResponseSize;
    }

    private TimeSpan CalculateExpiration(HttpCacheEntry entry)
    {
        // Calculate expiration based on HTTP cache headers
        var now = DateTimeOffset.UtcNow;

        // Check for explicit max-age
        if (entry.CacheControl?.MaxAge.HasValue == true)
        {
            var maxAge = entry.CacheControl.MaxAge.Value;
            var age = entry.GetAge();
            var remainingTime = maxAge - age;

            if (remainingTime > TimeSpan.Zero)
            {
                return ClampExpiration(remainingTime);
            }
        }

        // Check for Expires header
        if (entry.Expires.HasValue)
        {
            var expiresIn = entry.Expires.Value - now;
            if (expiresIn > TimeSpan.Zero)
            {
                return ClampExpiration(expiresIn);
            }
        }

        // Use default max age or heuristic freshness
        if (_httpOptions.DefaultMaxAge.HasValue)
        {
            return ClampExpiration(_httpOptions.DefaultMaxAge.Value);
        }

        // Heuristic freshness (10% of Last-Modified age)
        if (_httpOptions.AllowHeuristicFreshness && entry.LastModified.HasValue)
        {
            var lastModifiedAge = now - entry.LastModified.Value;
            var heuristicFreshness = TimeSpan.FromTicks(lastModifiedAge.Ticks / 10);

            if (heuristicFreshness <= _httpOptions.MaxHeuristicFreshness)
            {
                return ClampExpiration(heuristicFreshness);
            }
        }

        // Fallback to max heuristic freshness
        return ClampExpiration(_httpOptions.MaxHeuristicFreshness);
    }

    private TimeSpan ClampExpiration(TimeSpan expiration)
    {
        // Ensure expiration doesn't exceed L2 max expiration from storage options
        var maxExpiration = _storageOptions.L2MaxExpiration;
        if (expiration > maxExpiration)
        {
            _logger.LogDebug("Clamping HTTP cache expiration from {Original} to {Max}", expiration, maxExpiration);
            return maxExpiration;
        }

        // Ensure minimum expiration
        var minExpiration = _httpOptions.MinExpiration != default ? _httpOptions.MinExpiration : TimeSpan.FromSeconds(30);
        if (expiration < minExpiration)
        {
            return minExpiration;
        }

        return expiration;
    }

    private IEnumerable<string> GenerateTags(HttpCacheEntry entry)
    {
        var tags = new List<string>
        {
            "http-cache" // Universal tag for all HTTP cache entries
        };

        // Add method-based tag
        if (!string.IsNullOrEmpty(entry.Method))
        {
            tags.Add($"method:{entry.Method.ToUpperInvariant()}");
        }

        // Add URI-based tags
        if (!string.IsNullOrEmpty(entry.RequestUri))
        {
            try
            {
                var uri = new Uri(entry.RequestUri);

                // Add host tag
                tags.Add($"host:{uri.Host}");

                // Add path tag (for path-based invalidation)
                if (!string.IsNullOrEmpty(uri.AbsolutePath))
                {
                    tags.Add($"path:{uri.AbsolutePath}");

                    // Add parent path tags for hierarchical invalidation
                    var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var currentPath = "";
                    foreach (var segment in pathSegments.Take(pathSegments.Length - 1))
                    {
                        currentPath += "/" + segment;
                        tags.Add($"parent-path:{currentPath}");
                    }
                }
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Invalid URI format for cache entry: {RequestUri}", entry.RequestUri);
            }
        }

        // Add content-type tag if available
        if (entry.ContentHeaders.TryGetValue("Content-Type", out var contentTypes) && contentTypes.Length > 0)
        {
            var contentType = contentTypes[0].Split(';')[0].Trim();
            tags.Add($"content-type:{contentType}");
        }

        // Add status code tag
        tags.Add($"status:{(int)entry.StatusCode}");

        return tags;
    }
}

/// <summary>
/// Statistics for HTTP cache operations.
/// </summary>
public class HttpCacheStats
{
    public long Requests { get; init; }
    public long Hits { get; init; }
    public long Misses { get; init; }
    public double HitRatio { get; init; }
    public long Sets { get; init; }
    public long Removes { get; init; }
    public string StorageProviderName { get; init; } = "";
}