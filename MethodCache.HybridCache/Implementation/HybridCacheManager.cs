using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HybridCache.Implementation;

/// <summary>
/// Hybrid cache manager that uses the Infrastructure layer for storage operations
/// while maintaining method caching semantics and business logic.
/// </summary>
public class HybridCacheManager : IHybridCacheManager, IAsyncDisposable
{
    private readonly IStorageProvider _storageProvider;
    private readonly IMemoryStorage _l1Storage;
    private readonly IBackplane? _backplane;
    private readonly HybridCacheOptions _options;
    private readonly ILogger<HybridCacheManager> _logger;
    private readonly StripedLockPool _keyLevelLocks;

    // Statistics for hybrid-specific operations
    private long _stampedeProtectionActivations;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _refreshAheadTokens = new();

    // Cached objects for optimization
    private static readonly CacheMethodSettings CachedL2Settings = new() { Duration = TimeSpan.FromMinutes(1) };
    private static readonly object[] EmptyArgs = Array.Empty<object>();
    private const string L2DirectGetMethodName = "HybridL2DirectGet";

    // Disposal tracking
    private bool _disposed = false;

    // Helper properties for cleaner logic
    private bool ShouldUseL2 => _options.L2Enabled && _options.Strategy != HybridStrategy.L1Only;
    private bool ShouldUseL1 => _options.Strategy != HybridStrategy.L2Only;
    private bool IsL1OnlyMode => _options.Strategy == HybridStrategy.L1Only;

    public HybridCacheManager(
        IStorageProvider storageProvider,
        IMemoryStorage l1Storage,
        IBackplane? backplane,
        IOptions<HybridCacheOptions> options,
        ILogger<HybridCacheManager> logger)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _l1Storage = l1Storage ?? throw new ArgumentNullException(nameof(l1Storage));
        _backplane = backplane;
        _options = options.Value;
        _logger = logger;

        _keyLevelLocks = new StripedLockPool(128); // 128 stripes for good distribution

        // Subscribe to backplane invalidation events if available
        if (_backplane != null && _options.EnableBackplane)
        {
            _ = StartBackplaneListeningAsync();
            _logger.LogInformation("Hybrid cache backplane enabled for instance {InstanceId}", _options.InstanceId);
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string methodName,
        object[] args,
        Func<Task<T>> factory,
        CacheMethodSettings settings,
        ICacheKeyGenerator keyGenerator,
        bool requireIdempotent)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var stampede = settings.StampedeProtection;
        var distributedLockOptions = settings.DistributedLock;

        if (distributedLockOptions == null && stampede?.Mode == StampedeProtectionMode.DistributedLock)
        {
            distributedLockOptions = new DistributedLockOptions(stampede.RefreshAheadWindow ?? TimeSpan.FromSeconds(30), 1);
        }

        var useDistributedLock = distributedLockOptions != null;
        var lockTimeout = distributedLockOptions?.Timeout ?? TimeSpan.FromSeconds(30);

        try
        {
            // Try get from storage first (handles L1/L2 coordination internally)
            var cachedValue = await TryGetFromStorageAsync<T>(cacheKey);
            if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
            {
                _logger.LogDebug("Cache hit for key {Key}", cacheKey);
                return cachedValue.Value;
            }

            // Handle cache miss or refresh-ahead scenario
            if (useDistributedLock)
            {
                return await GetOrCreateWithLockAsync(cacheKey, factory, settings, lockTimeout);
            }
            else
            {
                // No locking, direct execution
                _logger.LogDebug("Cache miss, executing factory for key {Key}", cacheKey);
                var result = await factory();

                if (result != null)
                {
                    await SetToCacheWithTagsAsync(cacheKey, result, settings);
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in hybrid cache operation for method {MethodName}", methodName);

            // Fallback to direct execution
            return await factory();
        }
    }

    public async Task InvalidateByTagsAsync(params string[] tags)
    {
        if (tags == null || !tags.Any()) return;

        try
        {
            var invalidationTasks = new List<Task>();

            // Invalidate L1 cache for each tag
            if (_options.Strategy != HybridStrategy.L2Only)
            {
                foreach (var tag in tags)
                {
                    invalidationTasks.Add(_l1Storage.RemoveByTagAsync(tag));
                }
            }

            // Invalidate L2 cache for each tag
            if (ShouldUseL2)
            {
                foreach (var tag in tags)
                {
                    invalidationTasks.Add(_storageProvider.RemoveByTagAsync(tag));
                }
            }

            // Execute all invalidations concurrently
            await Task.WhenAll(invalidationTasks);

            // Publish invalidation event via backplane if enabled
            if (_backplane != null && _options.EnableBackplane)
            {
                var publishTasks = tags.Select(tag => _backplane.PublishTagInvalidationAsync(tag));
                await Task.WhenAll(publishTasks);
            }

            _logger.LogDebug("Invalidated cache entries for tags {Tags}", string.Join(", ", tags));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating cache by tags {Tags}", string.Join(", ", tags));
        }
    }

    public async Task InvalidateByKeysAsync(params string[] keys)
    {
        if (keys == null || keys.Length == 0) return;

        var normalizedKeys = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (normalizedKeys.Length == 0) return;

        try
        {
            // Use storage provider for key invalidation
            var tasks = normalizedKeys.Select(key => _storageProvider.RemoveAsync(key));
            await Task.WhenAll(tasks);

            // Publish invalidation events via backplane if enabled
            if (_backplane != null && _options.EnableBackplane)
            {
                var publishTasks = normalizedKeys.Select(key => _backplane.PublishInvalidationAsync(key));
                await Task.WhenAll(publishTasks);
            }

            _logger.LogDebug("Invalidated cache entries for keys {Keys}", string.Join(", ", normalizedKeys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating cache by keys {Keys}", string.Join(", ", keys));
            throw;
        }
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return Task.CompletedTask;

        try
        {
            // For pattern-based invalidation, we need to use L1 storage directly
            // since the pattern matching is not supported at the infrastructure level
            if (ShouldUseL1)
            {
                // This would require extending IMemoryStorage with pattern support
                // For now, we'll fall back to clearing the entire L1 cache
                _logger.LogWarning("Pattern-based invalidation not fully supported with infrastructure layer, clearing L1 cache for pattern {Pattern}", pattern);
                _l1Storage.Clear();
            }

            // For L2, we can't efficiently handle patterns without provider-specific logic
            _logger.LogDebug("Pattern invalidation for {Pattern} completed", pattern);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating cache by pattern {Pattern}", pattern);
            return Task.FromException(ex);
        }
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);

        try
        {
            var result = await _storageProvider.GetAsync<T>(cacheKey);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to get value for method {MethodName}", methodName);
            return default(T);
        }
    }

    // IHybridCacheManager specific methods - delegate to storage provider
    public async Task<T?> GetFromL1Async<T>(string key)
    {
        return await _l1Storage.GetAsync<T>(key);
    }

    public async Task<T?> GetFromL2Async<T>(string key)
    {
        if (!ShouldUseL2) return default;

        try
        {
            return await _storageProvider.GetAsync<T>(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting value from L2 for key {Key}", key);
            return default;
        }
    }

    public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration)
    {
        if (_options.Strategy == HybridStrategy.L2Only) return;

        await _l1Storage.SetAsync(key, value, expiration);
    }

    public async Task SetInL1Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        if (_options.Strategy == HybridStrategy.L2Only) return;

        await _l1Storage.SetAsync(key, value, expiration, tags);
    }

    public async Task SetInL2Async<T>(string key, T value, TimeSpan expiration)
    {
        if (!ShouldUseL2) return;

        await _storageProvider.SetAsync(key, value, expiration);
    }

    public async Task SetInBothAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration)
    {
        var tasks = new List<Task>();

        if (ShouldUseL1)
        {
            tasks.Add(_l1Storage.SetAsync(key, value, l1Expiration));
        }

        if (ShouldUseL2)
        {
            tasks.Add(_storageProvider.SetAsync(key, value, l2Expiration));
        }

        await Task.WhenAll(tasks);
    }

    public async Task InvalidateL1Async(string key)
    {
        await _l1Storage.RemoveAsync(key);
    }

    public async Task InvalidateL2Async(string key)
    {
        if (!ShouldUseL2) return;

        await _storageProvider.RemoveAsync(key);
    }

    public async Task InvalidateBothAsync(string key)
    {
        var tasks = new List<Task>();

        if (ShouldUseL1)
        {
            tasks.Add(_l1Storage.RemoveAsync(key));
        }

        if (ShouldUseL2)
        {
            tasks.Add(_storageProvider.RemoveAsync(key));
        }

        await Task.WhenAll(tasks);
    }

    public async Task WarmL1CacheAsync(params string[] keys)
    {
        if (!ShouldUseL1 || !ShouldUseL2 || keys.Length == 0) return;

        try
        {
            foreach (var key in keys)
            {
                var value = await _storageProvider.GetAsync<object>(key);
                if (value != null)
                {
                    var l1Expiration = CalculateL1Expiration(_options.L2DefaultExpiration);
                    await _l1Storage.SetAsync(key, value, l1Expiration);
                }
            }

            _logger.LogDebug("Warmed L1 cache with {Count} keys", keys.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error warming L1 cache");
        }
    }

    public async Task<HybridCacheStats> GetStatsAsync()
    {
        try
        {
            var l1Stats = _l1Storage.GetStats();
            var storageStats = await _storageProvider.GetStatsAsync();

            return new HybridCacheStats
            {
                L1Hits = l1Stats.Hits,
                L1Misses = l1Stats.Misses,
                L1Entries = l1Stats.EntryCount,
                L1Evictions = l1Stats.Evictions,
                L2Hits = storageStats?.GetOperations ?? 0, // Approximation
                L2Misses = 0, // Would need to track this separately
                TagMappingCount = l1Stats.TagMappingCount,
                UniqueTagCount = 0, // Would need to track this separately
                EfficientTagInvalidationEnabled = _options.EnableEfficientL1TagInvalidation,
                BackplaneMessagesSent = 0, // Would need to track this separately
                BackplaneMessagesReceived = 0 // Would need to track this separately
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting hybrid cache stats");
            return new HybridCacheStats();
        }
    }

    public async Task EvictFromL1Async(string key)
    {
        await _l1Storage.RemoveAsync(key);
    }

    public Task SyncL1CacheAsync()
    {
        // This would require coordinating with other instances
        // For now, we'll just clear L1 to force refresh
        _l1Storage.Clear();
        _logger.LogDebug("Synchronized L1 cache by clearing it");
        return Task.CompletedTask;
    }

    private async Task<(bool HasValue, T Value, TimeSpan? TimeToLive)> TryGetFromStorageAsync<T>(string key)
    {
        // Try L1 first if enabled
        if (ShouldUseL1)
        {
            var l1Value = await _l1Storage.GetAsync<T>(key);
            if (l1Value != null)
            {
                return (true, l1Value, null); // L1 doesn't provide TTL
            }
        }

        // Try L2 if enabled and L1 missed
        if (ShouldUseL2)
        {
            var l2Value = await _storageProvider.GetAsync<T>(key);
            if (l2Value != null)
            {
                // Warm L1 cache
                if (ShouldUseL1)
                {
                    var l1Expiration = CalculateL1Expiration(_options.L2DefaultExpiration);
                    await _l1Storage.SetAsync(key, l2Value, l1Expiration);
                }

                return (true, l2Value, null); // Storage provider doesn't expose TTL directly
            }
        }

        return (false, default(T)!, null);
    }

    private async Task<T> GetOrCreateWithLockAsync<T>(string cacheKey, Func<Task<T>> factory, CacheMethodSettings settings, TimeSpan lockTimeout)
    {
        var lockKey = $"lock:{cacheKey}";

        using var lockHandle = await _keyLevelLocks.AcquireAsync(lockKey);
        Interlocked.Increment(ref _stampedeProtectionActivations);

        try
        {
            // Double-check cache after acquiring lock
            var cachedValue = await TryGetFromStorageAsync<T>(cacheKey);
            if (cachedValue.HasValue && !ShouldRefreshAhead(cachedValue.TimeToLive, settings))
            {
                _logger.LogDebug("Cache hit after lock acquisition for key {Key}", cacheKey);
                return cachedValue.Value;
            }

            // Execute factory and cache result
            _logger.LogDebug("Cache miss, executing factory for key {Key}", cacheKey);
            var result = await factory();

            if (result != null)
            {
                await SetToCacheWithTagsAsync(cacheKey, result, settings);
            }

            return result;
        }
        finally
        {
            // Lock will be automatically released by using statement
        }
    }

    private async Task SetToCacheWithTagsAsync<T>(string key, T value, CacheMethodSettings settings)
    {
        var l1Expiration = CalculateL1Expiration(settings.Duration ?? _options.L2DefaultExpiration);
        var l2Expiration = settings.Duration ?? _options.L2DefaultExpiration;
        var tags = settings.Tags;

        var tasks = new List<Task>();

        // Set in L1 if enabled
        if (ShouldUseL1)
        {
            tasks.Add(_l1Storage.SetAsync(key, value, l1Expiration, tags));
        }

        // Set in L2 if enabled
        if (ShouldUseL2)
        {
            if (_options.EnableAsyncL2Writes)
            {
                // Fire and forget for async writes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _storageProvider.SetAsync(key, value, l2Expiration, tags);
                        _logger.LogDebug("Async L2 write completed for key {Key}", key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in async L2 write for key {Key}", key);
                    }
                });
            }
            else
            {
                // Synchronous L2 write
                tasks.Add(_storageProvider.SetAsync(key, value, l2Expiration, tags));
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogDebug("Set key {Key} in hybrid storage", key);
    }

    private TimeSpan CalculateL1Expiration(TimeSpan originalExpiration)
    {
        var l1Expiration = TimeSpan.FromTicks(Math.Min(
            originalExpiration.Ticks,
            _options.L1MaxExpiration.Ticks));

        return TimeSpan.FromTicks(Math.Max(
            l1Expiration.Ticks,
            _options.L1DefaultExpiration.Ticks));
    }

    private static bool ShouldRefreshAhead(TimeSpan? timeToLive, CacheMethodSettings settings)
    {
        if (timeToLive == null) return false;

        var configuredRefreshAhead = settings.RefreshAhead;
        if (configuredRefreshAhead is TimeSpan refreshAhead && refreshAhead > TimeSpan.Zero && timeToLive <= refreshAhead)
        {
            return true;
        }

        var stampede = settings.StampedeProtection;
        if (stampede == null) return false;

        switch (stampede.Mode)
        {
            case StampedeProtectionMode.RefreshAhead:
                var window = stampede.RefreshAheadWindow ?? configuredRefreshAhead;
                return window.HasValue && window.Value > TimeSpan.Zero && timeToLive <= window;
            case StampedeProtectionMode.Probabilistic:
                var duration = settings.Duration ?? TimeSpan.Zero;
                if (duration <= TimeSpan.Zero) return false;

                var remainingRatio = Math.Clamp(timeToLive.Value.TotalSeconds / duration.TotalSeconds, 0d, 1d);
                var beta = stampede.Beta <= 0 ? 1d : stampede.Beta;
                var probability = Math.Exp(-beta * (1 - remainingRatio));
                var sample = Random.Shared.NextDouble();
                return sample > probability;
            default:
                return false;
        }
    }

    private async Task StartBackplaneListeningAsync()
    {
        if (_backplane == null) return;

        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _backplane.SubscribeAsync(OnBackplaneMessageReceived);
                _logger.LogInformation("Successfully started backplane listening on attempt {Attempt}", attempt);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start backplane listening on attempt {Attempt} of {MaxRetries}", attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delay);
                }
            }
        }

        _logger.LogError("Failed to start backplane listening after {MaxRetries} attempts", maxRetries);
    }

    private async Task OnBackplaneMessageReceived(BackplaneMessage message)
    {
        try
        {
            // Ignore messages from our own instance
            if (message.InstanceId == _options.InstanceId)
                return;

            switch (message.Type)
            {
                case BackplaneMessageType.KeyInvalidation when message.Key != null:
                    await _l1Storage.RemoveAsync(message.Key);
                    _logger.LogDebug("Processed backplane key invalidation for {Key}", message.Key);
                    break;

                case BackplaneMessageType.TagInvalidation when message.Tag != null:
                    await _l1Storage.RemoveByTagAsync(message.Tag);
                    _logger.LogDebug("Processed backplane tag invalidation for {Tag}", message.Tag);
                    break;

                case BackplaneMessageType.ClearAll:
                    _l1Storage.Clear();
                    _logger.LogDebug("Processed backplane clear all");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing backplane message");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            // Cancel all refresh-ahead operations
            foreach (var cts in _refreshAheadTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _refreshAheadTokens.Clear();

            // Unsubscribe from backplane
            if (_backplane != null)
            {
                await _backplane.UnsubscribeAsync();
            }

            _logger.LogDebug("Hybrid cache manager disposed");
        }
        finally
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Simple striped lock implementation for distributed locking simulation.
/// </summary>
internal class StripedLockPool
{
    private readonly SemaphoreSlim[] _locks;
    private readonly int _stripeCount;

    public StripedLockPool(int stripeCount)
    {
        _stripeCount = stripeCount;
        _locks = new SemaphoreSlim[stripeCount];
        for (int i = 0; i < stripeCount; i++)
        {
            _locks[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async Task<IDisposable> AcquireAsync(string key)
    {
        var stripe = Math.Abs(key.GetHashCode()) % _stripeCount;
        var semaphore = _locks[stripe];
        await semaphore.WaitAsync();
        return new SemaphoreReleaser(semaphore);
    }

    private class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}