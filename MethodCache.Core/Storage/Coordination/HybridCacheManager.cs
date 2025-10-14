using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Coordination;

/// <summary>
/// Implementation of IHybridCacheManager that coordinates L1/L2/L3 cache operations.
/// </summary>
public class HybridCacheManager : IHybridCacheManager
{
    private readonly IStorageProvider _storageProvider;
    private readonly IMemoryStorage _l1Storage;
    private readonly IStorageProvider? _l2Storage;
    private readonly IPersistentStorageProvider? _l3Storage;
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly ILogger<HybridCacheManager> _logger;
    private readonly StorageOptions _options;

    public HybridCacheManager(
        IStorageProvider storageProvider,
        IMemoryStorage l1Storage,
        ICacheKeyGenerator keyGenerator,
        ILogger<HybridCacheManager> logger,
        IOptions<StorageOptions> options,
        IStorageProvider? l2Storage = null,
        IPersistentStorageProvider? l3Storage = null)
    {
        _storageProvider = storageProvider;
        _l1Storage = l1Storage;
        _l2Storage = l2Storage;
        _l3Storage = l3Storage;
        _keyGenerator = keyGenerator;
        _logger = logger;
        _options = options.Value;
    }

    // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var cacheKey = keyGenerator.GenerateKey(methodName, args, policy);

        var cached = await _storageProvider.GetAsync<T>(cacheKey);
        if (cached != null)
        {
            return cached;
        }

        var result = await factory();
        if (result != null)
        {
            var expiration = policy.Duration ?? _options.L1DefaultExpiration;
            await _storageProvider.SetAsync(cacheKey, result, expiration, policy.Tags).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var cacheKey = keyGenerator.GenerateKey(methodName, args, policy);
        return await _storageProvider.GetAsync<T>(cacheKey);
    }

    public async ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
    {
        return await _storageProvider.GetAsync<T>(cacheKey);
    }

    public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy)
    {
        var cached = await _storageProvider.GetAsync<T>(cacheKey);
        if (cached != null)
        {
            return cached;
        }

        var result = await factory();
        if (result != null)
        {
            var expiration = policy.Duration ?? _options.L1DefaultExpiration;
            await _storageProvider.SetAsync(cacheKey, result, expiration, policy.Tags).ConfigureAwait(false);
        }

        return result;
    }

    public async Task InvalidateByTagsAsync(params string[] tags)
    {
        var tasks = tags.Select(tag => _storageProvider.RemoveByTagAsync(tag).AsTask());
        await Task.WhenAll(tasks);
    }

    public async Task InvalidateByKeysAsync(params string[] keys)
    {
        var tasks = keys.Select(key => _storageProvider.RemoveAsync(key).AsTask());
        await Task.WhenAll(tasks);
    }

    public async Task InvalidateByTagPatternAsync(string pattern)
    {
        // This would need pattern matching implementation
        // For now, just log that it's not implemented
        _logger.LogWarning("InvalidateByTagPatternAsync not implemented for pattern: {Pattern}", pattern);
        await Task.CompletedTask;
    }

    // IHybridCacheManager implementation - layer-specific operations
    public async Task<T?> GetFromL1Async<T>(string key)
    {
        return await _l1Storage.GetAsync<T>(key);
    }

    public async Task<T?> GetFromL2Async<T>(string key)
    {
        if (_l2Storage == null)
        {
            return default;
        }
        return await _l2Storage.GetAsync<T>(key);
    }

    public async Task<T?> GetFromL3Async<T>(string key)
    {
        if (_l3Storage == null)
        {
            return default;
        }
        return await _l3Storage.GetAsync<T>(key);
    }

    public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration)
    {
        await _l1Storage.SetAsync(key, value, expiration);
    }

    public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        await _l1Storage.SetAsync(key, value, expiration, tags);
    }

    public async Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
    {
        if (_l2Storage != null)
        {
            await _l2Storage.SetAsync(key, value, expiration);
        }
    }

    public async Task SetInL3Async<T>(string key, T value, TimeSpan expiration)
    {
        if (_l3Storage != null)
        {
            await _l3Storage.SetAsync(key, value, expiration);
        }
    }

    public async Task SetInBothAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration)
    {
        await Task.WhenAll(
            SetInL1Async(key, value, l1Expiration),
            SetInL2Async(key, value, l2Expiration)
        );
    }

    public async Task SetInAllAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration, TimeSpan l3Expiration)
    {
        await Task.WhenAll(
            SetInL1Async(key, value, l1Expiration),
            SetInL2Async(key, value, l2Expiration),
            SetInL3Async(key, value, l3Expiration)
        );
    }

    public async Task InvalidateL1Async(string key)
    {
        await _l1Storage.RemoveAsync(key);
    }

    public async Task InvalidateL2Async(string key)
    {
        if (_l2Storage != null)
        {
            await _l2Storage.RemoveAsync(key);
        }
    }

    public async Task InvalidateL3Async(string key)
    {
        if (_l3Storage != null)
        {
            await _l3Storage.RemoveAsync(key);
        }
    }

    public async Task InvalidateBothAsync(string key)
    {
        await Task.WhenAll(
            InvalidateL1Async(key),
            InvalidateL2Async(key)
        );
    }

    public async Task InvalidateAllAsync(string key)
    {
        await Task.WhenAll(
            InvalidateL1Async(key),
            InvalidateL2Async(key),
            InvalidateL3Async(key)
        );
    }

    public async Task WarmL1CacheAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            // Try to get from L2/L3 and warm L1
            if (_l2Storage != null)
            {
                var value = await _l2Storage.GetAsync<object>(key);
                if (value != null)
                {
                    await _l1Storage.SetAsync(key, value, _options.L1DefaultExpiration);
                    continue;
                }
            }

            if (_l3Storage != null)
            {
                var value = await _l3Storage.GetAsync<object>(key);
                if (value != null)
                {
                    await _l1Storage.SetAsync(key, value, _options.L1DefaultExpiration);
                }
            }
        }
    }

    public async Task<HybridCacheStats> GetStatsAsync()
    {
        // If the storage provider is a StorageCoordinator, get stats from it
        if (_storageProvider is StorageCoordinator coordinator)
        {
            var stats = await coordinator.GetStatsAsync();
            if (stats?.AdditionalStats != null)
            {
                return new HybridCacheStats
                {
                    L1Hits = stats.AdditionalStats.TryGetValue("L1Hits", out var l1Hits) ? Convert.ToInt64(l1Hits) : 0,
                    L1Misses = stats.AdditionalStats.TryGetValue("L1Misses", out var l1Misses) ? Convert.ToInt64(l1Misses) : 0,
                    L2Hits = stats.AdditionalStats.TryGetValue("L2Hits", out var l2Hits) ? Convert.ToInt64(l2Hits) : 0,
                    L2Misses = stats.AdditionalStats.TryGetValue("L2Misses", out var l2Misses) ? Convert.ToInt64(l2Misses) : 0,
                    L3Hits = stats.AdditionalStats.TryGetValue("L3Hits", out var l3Hits) ? Convert.ToInt64(l3Hits) : 0,
                    L3Misses = stats.AdditionalStats.TryGetValue("L3Misses", out var l3Misses) ? Convert.ToInt64(l3Misses) : 0,
                    L1Entries = _l1Storage.GetStats()?.EntryCount ?? 0,
                    L1Evictions = _l1Storage.GetStats()?.Evictions ?? 0,
                    BackplaneMessagesSent = stats.AdditionalStats.TryGetValue("BackplaneMessagesSent", out var sent) ? Convert.ToInt64(sent) : 0,
                    BackplaneMessagesReceived = stats.AdditionalStats.TryGetValue("BackplaneMessagesReceived", out var received) ? Convert.ToInt64(received) : 0,
                    TagMappingCount = _l1Storage.GetStats()?.TagMappingCount ?? 0,
                    UniqueTagCount = 0, // Would need to calculate this
                    EfficientTagInvalidationEnabled = true
                };
            }
        }

        // Fallback to basic stats from L1
        var l1Stats = _l1Storage.GetStats();
        return new HybridCacheStats
        {
            L1Hits = l1Stats?.Hits ?? 0,
            L1Misses = l1Stats?.Misses ?? 0,
            L1Entries = l1Stats?.EntryCount ?? 0,
            L1Evictions = l1Stats?.Evictions ?? 0,
            TagMappingCount = l1Stats?.TagMappingCount ?? 0,
            EfficientTagInvalidationEnabled = true
        };
    }

    public async Task EvictFromL1Async(string key)
    {
        await _l1Storage.RemoveAsync(key);
    }

    public async Task SyncL1CacheAsync()
    {
        // This would need coordination with backplane
        // For now, just log that it's not implemented
        _logger.LogDebug("SyncL1CacheAsync called - not implemented");
        await Task.CompletedTask;
    }
}
