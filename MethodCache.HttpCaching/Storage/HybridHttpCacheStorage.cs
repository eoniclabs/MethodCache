using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration;
using MethodCache.Core.Storage;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Storage;

/// <summary>
/// HTTP cache storage implementation that uses the hybrid storage infrastructure
/// to provide L1 (memory) + L2 (distributed) caching capabilities.
/// </summary>
public class HybridHttpCacheStorage : IHttpCacheStorage
{
    private readonly IStorageProvider _storageProvider;
    private readonly ILogger<HybridHttpCacheStorage> _logger;
    private readonly CacheBehaviorOptions _behavior;
    private readonly CacheFreshnessOptions _freshness;
    private readonly HttpCacheStorageOptions _httpStorage;
    private readonly StorageOptions _hybridStorageOptions;

    private long _requests;
    private long _hits;
    private long _misses;
    private long _sets;
    private long _removes;

    public HybridHttpCacheStorage(
        IStorageProvider storageProvider,
        IOptions<HttpCacheOptions> cacheOptions,
        IOptions<HttpCacheStorageOptions> storageOptions,
        IOptions<StorageOptions> hybridStorageOptions,
        ILogger<HybridHttpCacheStorage> logger)
    {
        _storageProvider = storageProvider;
        _logger = logger;
        var options = cacheOptions.Value;
        _behavior = options.Behavior;
        _freshness = options.Freshness;
        _httpStorage = storageOptions.Value;
        _hybridStorageOptions = hybridStorageOptions.Value;
    }

    public async ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requests);

        try
        {
            var entry = await _storageProvider.GetAsync<HttpCacheEntry>(key, cancellationToken).ConfigureAwait(false);

            if (entry != null)
            {
                if (IsEntrySizeValid(entry))
                {
                    Interlocked.Increment(ref _hits);
                    _logger.LogDebug("HTTP cache hit for key {Key}", key);
                    return entry;
                }

                _logger.LogWarning("HTTP cache entry {Key} exceeded size limits, removing", key);
                await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default)
    {
        if (!IsEntrySizeValid(entry))
        {
            _logger.LogDebug("HTTP cache entry {Key} exceeds maximum response size ({Size} bytes), not caching", key, entry.Content.Length);
            return;
        }

        try
        {
            var expiration = CalculateExpiration(entry);
            var tags = GenerateTags(entry);

            await _storageProvider.SetAsync(key, entry, expiration, tags, cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _sets);
            _logger.LogDebug("Stored HTTP cache entry {Key} with expiration {Expiration} and tags {Tags}", key, expiration, string.Join(", ", tags));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing HTTP cache entry for key {Key}", key);
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageProvider.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _removes);
            _logger.LogDebug("Removed HTTP cache entry {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing HTTP cache entry for key {Key}", key);
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageProvider.RemoveByTagAsync(HttpCacheTags.AllEntries, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Cleared all HTTP cache entries using tag invalidation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing HTTP cache");
        }
    }

    public async ValueTask InvalidateByUriAsync(string uriPattern, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageProvider.RemoveByTagAsync(HttpCacheTags.ForUriPattern(uriPattern), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Invalidated HTTP cache entries for URI pattern {UriPattern}", uriPattern);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating HTTP cache entries for URI pattern {UriPattern}", uriPattern);
        }
    }

    public async ValueTask InvalidateByMethodAsync(string method, CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageProvider.RemoveByTagAsync(HttpCacheTags.ForMethod(method), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Invalidated HTTP cache entries for method {Method}", method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating HTTP cache entries for method {Method}", method);
        }
    }

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
        if (_httpStorage.MaxResponseSize <= 0)
        {
            return true;
        }

        return entry.Content.Length <= _httpStorage.MaxResponseSize;
    }

    private TimeSpan CalculateExpiration(HttpCacheEntry entry)
    {
        var now = DateTimeOffset.UtcNow;

        if (_behavior.RespectCacheControl && entry.CacheControl?.MaxAge.HasValue == true)
        {
            var remaining = entry.CacheControl.MaxAge.Value - entry.GetAge();
            if (remaining > TimeSpan.Zero)
            {
                return ClampExpiration(remaining);
            }
        }

        if (_behavior.IsSharedCache && _behavior.RespectCacheControl && entry.CacheControl?.SharedMaxAge.HasValue == true)
        {
            var remaining = entry.CacheControl.SharedMaxAge.Value - entry.GetAge();
            if (remaining > TimeSpan.Zero)
            {
                return ClampExpiration(remaining);
            }
        }

        if (entry.Expires.HasValue)
        {
            var expiresIn = entry.Expires.Value - now;
            if (expiresIn > TimeSpan.Zero)
            {
                return ClampExpiration(expiresIn);
            }
        }

        if (_freshness.DefaultMaxAge.HasValue)
        {
            return ClampExpiration(_freshness.DefaultMaxAge.Value);
        }

        if (_freshness.AllowHeuristicFreshness && entry.LastModified.HasValue)
        {
            var lastModifiedAge = now - entry.LastModified.Value;
            var heuristicFreshness = TimeSpan.FromTicks(lastModifiedAge.Ticks / 10);
            if (heuristicFreshness > _freshness.MaxHeuristicFreshness)
            {
                heuristicFreshness = _freshness.MaxHeuristicFreshness;
            }

            return ClampExpiration(heuristicFreshness);
        }

        return ClampExpiration(_freshness.MaxHeuristicFreshness);
    }

    private TimeSpan ClampExpiration(TimeSpan expiration)
    {
        if (expiration > _hybridStorageOptions.L2MaxExpiration)
        {
            _logger.LogDebug("Clamping HTTP cache expiration from {Original} to {Max}", expiration, _hybridStorageOptions.L2MaxExpiration);
            expiration = _hybridStorageOptions.L2MaxExpiration;
        }

        if (expiration < _freshness.MinExpiration)
        {
            return _freshness.MinExpiration;
        }

        return expiration;
    }

    private IEnumerable<string> GenerateTags(HttpCacheEntry entry)
    {
        // Estimate tag count to avoid resizing
        var estimatedCount = 5; // AllEntries + Method + Host + Path + Status

        if (!string.IsNullOrEmpty(entry.RequestUri) && Uri.TryCreate(entry.RequestUri, UriKind.Absolute, out var tempUri))
        {
            if (!string.IsNullOrEmpty(tempUri.AbsolutePath))
            {
                var segmentCount = tempUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
                estimatedCount += segmentCount; // Parent paths
            }
        }

        var tags = new List<string>(estimatedCount) { HttpCacheTags.AllEntries };

        if (!string.IsNullOrEmpty(entry.Method))
        {
            tags.Add(HttpCacheTags.ForMethod(entry.Method));
        }

        if (!string.IsNullOrEmpty(entry.RequestUri))
        {
            if (Uri.TryCreate(entry.RequestUri, UriKind.Absolute, out var uri))
            {
                tags.Add(HttpCacheTags.ForHost(uri.Host));

                if (!string.IsNullOrEmpty(uri.AbsolutePath))
                {
                    tags.Add(HttpCacheTags.ForPath(uri.AbsolutePath));

                    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length > 1)
                    {
                        // Use StringBuilder to avoid string concatenation allocations
                        var pathBuilder = new System.Text.StringBuilder();
                        for (var i = 0; i < segments.Length - 1; i++)
                        {
                            pathBuilder.Append('/').Append(segments[i]);
                            tags.Add(HttpCacheTags.ForParentPath(pathBuilder.ToString()));
                        }
                    }
                }
            }
        }

        if (entry.ContentHeaders.TryGetValue("Content-Type", out var contentTypes) && contentTypes.Length > 0)
        {
            var contentType = contentTypes[0].Split(';')[0].Trim();
            tags.Add(HttpCacheTags.ForContentType(contentType));
        }

        tags.Add(HttpCacheTags.ForStatus(entry.StatusCode));
        return tags;
    }
}

public class HttpCacheStats
{
    public long Requests { get; init; }
    public long Hits { get; init; }
    public long Misses { get; init; }
    public double HitRatio { get; init; }
    public long Sets { get; init; }
    public long Removes { get; init; }
    public string StorageProviderName { get; init; } = string.Empty;
}
