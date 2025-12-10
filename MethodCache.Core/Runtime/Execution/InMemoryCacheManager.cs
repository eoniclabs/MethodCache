using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Infrastructure.Utilities;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Runtime.Execution
{
    /// <summary>
    /// Enhanced in-memory cache manager that implements both ICacheManager and IMemoryCache interfaces.
    /// Provides advanced features like eviction policies, statistics, and configurable memory usage calculation.
    /// </summary>
    public class InMemoryCacheManager : ICacheManager, IMemoryCache
    {
        private class EnhancedCacheEntry
        {
            private long _accessCount;

            public object Value { get; init; } = null!;
            public HashSet<string>? Tags { get; init; }
            public DateTimeOffset AbsoluteExpiration { get; set; }
            public DateTime CreatedAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
            public long AccessCount => _accessCount;
            public CacheEntryPolicy Policy { get; init; } = CacheEntryPolicy.Empty;

            public bool IsExpired => DateTime.UtcNow > AbsoluteExpiration;

            public void UpdateAccess()
            {
                LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _accessCount);
            }
        }

        private sealed record CacheEntryPolicy(
            TimeSpan? Duration,
            TimeSpan? SlidingExpiration,
            TimeSpan? RefreshAhead,
            StampedeProtectionOptions? StampedeProtection,
            DistributedLockOptions? DistributedLock,
            ICacheMetrics? Metrics)
        {
            internal static readonly CacheEntryPolicy Empty = new(null, null, null, null, null, null);
        }

        private sealed class DistributedLockState
        {
            public DistributedLockState(SemaphoreSlim semaphore, int maxConcurrency)
            {
                Semaphore = semaphore;
                MaxConcurrency = maxConcurrency;
            }

            public SemaphoreSlim Semaphore { get; }
            public int MaxConcurrency { get; }
        }

        private readonly ConcurrentDictionary<string, EnhancedCacheEntry> _cache = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _singleFlightGates = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _lightweightGates = new(); // Lightweight coordination gate per cache key
        private readonly ConcurrentDictionary<string, DistributedLockState> _distributedLocks = new();
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly MemoryCacheOptions _options;
        private readonly Timer? _cleanupTimer;
        private readonly SemaphoreSlim _evictionSemaphore;
        private readonly MemoryUsageCalculator _memoryCalculator;

        // Tag reverse index for O(1) tag invalidation
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new();
        private const int MaxTagMappings = 100000; // Prevent unbounded growth
        private int _currentTagMappings = 0;
        private int _cleanupScheduled = 0;

        // Statistics
        private long _hits;
        private long _misses;
        private long _evictions;
        private bool _disposed = false;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider, IOptions<MemoryCacheOptions>? options = null)
        {
            _metricsProvider = metricsProvider;
            _options = options?.Value ?? new MemoryCacheOptions();
            _evictionSemaphore = new SemaphoreSlim(1, 1);
            _memoryCalculator = new MemoryUsageCalculator(_options);
            
            // Start cleanup timer for expired entries if enabled
            if (_options.EnableBackgroundCleanup)
            {
                _cleanupTimer = new Timer(CleanupExpiredEntries, null, _options.CleanupInterval, _options.CleanupInterval);
            }
        }

        #region ICacheManager Implementation

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            if (tags == null || !tags.Any()) return Task.CompletedTask;

            var keysToRemove = new HashSet<string>();

            // Lock-free O(1) reverse index lookup
            foreach (var tag in tags)
            {
                if (_tagToKeys.TryGetValue(tag, out var tagKeys))
                {
                    // Snapshot the keys - ConcurrentDictionary.Keys is thread-safe
                    foreach (var key in tagKeys.Keys)
                    {
                        keysToRemove.Add(key);
                    }
                }
            }

            // Remove all collected keys completely
            foreach (var key in keysToRemove)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                }
            }

            return Task.CompletedTask;
        }

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            if (keys == null || keys.Length == 0)
            {
                return Task.CompletedTask;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                }
            }

            return Task.CompletedTask;
        }

        public async Task InvalidateByTagPatternAsync(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return;
            }

            Regex regex;
            try
            {
                regex = WildcardToRegex(pattern);
            }
            catch (ArgumentException)
            {
                return; // Invalid pattern - no-op to avoid throwing in hot path
            }

            var matchingTags = _tagToKeys.Keys.Where(tag => regex.IsMatch(tag)).ToArray();
            if (matchingTags.Length == 0)
            {
                return;
            }

            await InvalidateByTagsAsync(matchingTags).ConfigureAwait(false);
        }

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        /// <summary>
        /// Static cache for converted CacheEntryPolicy objects. Uses ConditionalWeakTable to:
        /// 1. Avoid repeated allocations of CacheEntryPolicy on every cache miss
        /// 2. Automatically clean up entries when the CacheRuntimePolicy is garbage collected
        ///
        /// This cache is intentionally static and shared across all InMemoryCacheManager instances.
        /// Since CacheRuntimePolicy objects are typically singletons (created once per decorated method
        /// and cached in the generated decorator), sharing the converted policies is safe and beneficial.
        /// The ConditionalWeakTable ensures no memory leaks - when a CacheRuntimePolicy is collected,
        /// its associated CacheEntryPolicy is also eligible for collection.
        /// </summary>
        private static readonly ConditionalWeakTable<CacheRuntimePolicy, CacheEntryPolicy> _policyCache = new();

        private static CacheEntryPolicy GetCacheEntryPolicy(CacheRuntimePolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            return _policyCache.GetValue(policy, static p => CreatePolicyFromRuntimePolicy(p));
        }

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var key = keyGenerator.GenerateKey(methodName, args, policy);

            var cachedResult = await GetAsyncInternal<T>(key, updateStatistics: false);
            if (cachedResult != null)
            {
                _metricsProvider.CacheHit(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                return cachedResult;
            }
            
            var entryPolicy = GetCacheEntryPolicy(policy);
            var tags = policy.Tags.Count > 0 ? policy.Tags.ToArray() : Array.Empty<string>();

            if (!RequiresSingleFlight(entryPolicy))
            {
                // Lightweight coordination: prevents duplicate work without TCS overhead
                // Track if we're the coordinator (executing factory) or a waiter (getting cached result from coordinator)
                var isCoordinator = false;
                var result = await ExecuteWithLightweightCoordination<T>(key, async () =>
                {
                    isCoordinator = true;
                    var factoryResult = await factory().ConfigureAwait(false);

                    if (factoryResult != null)
                    {
                        var expiration = policy.Duration ?? _options.DefaultExpiration;
                        var effectiveExpiration = expiration > _options.MaxExpiration
                            ? _options.MaxExpiration
                            : expiration;
                        await SetAsync(key, factoryResult, effectiveExpiration, tags, entryPolicy).ConfigureAwait(false);
                    }

                    return (object)factoryResult!;
                }).ConfigureAwait(false);

                // Track metrics based on whether we coordinated or waited
                if (isCoordinator)
                {
                    _metricsProvider.CacheMiss(methodName);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                }
                else
                {
                    // Waiter got result from coordinator - treat as cache hit
                    _metricsProvider.CacheHit(methodName);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _hits);
                    }
                }

                return result;
            }

            return await ExecuteWithSingleFlight(key, async () =>
            {
                var result = await factory().ConfigureAwait(false);

                if (result != null)
                {
                    var expiration = policy.Duration ?? _options.DefaultExpiration;
                    var effectiveExpiration = expiration > _options.MaxExpiration
                        ? _options.MaxExpiration
                        : expiration;
                    await SetAsync(key, result, effectiveExpiration, tags, entryPolicy).ConfigureAwait(false);
                }

                _metricsProvider.CacheMiss(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _misses);
                }

                return result!;
            }, methodName, entryPolicy).ConfigureAwait(false);
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return GetAsyncInternal<T>(key, updateStatistics: false);
        }

        public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("Cache key is required", nameof(cacheKey));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var cachedResult = await GetAsyncInternal<T>(cacheKey, updateStatistics: false);
            if (cachedResult != null)
            {
                _metricsProvider.CacheHit(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }
                return cachedResult;
            }

            var entryPolicy = GetCacheEntryPolicy(policy);
            var tags = policy.Tags.Count > 0 ? policy.Tags.ToArray() : Array.Empty<string>();

            if (!RequiresSingleFlight(entryPolicy))
            {
                // Lightweight coordination: prevents duplicate work without TCS overhead
                // Track if we're the coordinator (executing factory) or a waiter (getting cached result from coordinator)
                var isCoordinator = false;
                var result = await ExecuteWithLightweightCoordination<T>(cacheKey, async () =>
                {
                    isCoordinator = true;
                    var factoryResult = await factory().ConfigureAwait(false);

                    if (factoryResult != null)
                    {
                        var expiration = policy.Duration ?? _options.DefaultExpiration;
                        var effectiveExpiration = expiration > _options.MaxExpiration
                            ? _options.MaxExpiration
                            : expiration;
                        await SetAsync(cacheKey, factoryResult, effectiveExpiration, tags, entryPolicy).ConfigureAwait(false);
                    }

                    return (object)factoryResult!;
                }).ConfigureAwait(false);

                // Track metrics based on whether we coordinated or waited
                if (isCoordinator)
                {
                    _metricsProvider.CacheMiss(methodName);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                }
                else
                {
                    // Waiter got result from coordinator - treat as cache hit
                    _metricsProvider.CacheHit(methodName);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _hits);
                    }
                }

                return result;
            }

            return await ExecuteWithSingleFlight(cacheKey, async () =>
            {
                var result = await factory().ConfigureAwait(false);

                if (result != null)
                {
                    var expiration = policy.Duration ?? _options.DefaultExpiration;
                    var effectiveExpiration = expiration > _options.MaxExpiration
                        ? _options.MaxExpiration
                        : expiration;
                    await SetAsync(cacheKey, result, effectiveExpiration, tags, entryPolicy).ConfigureAwait(false);
                }

                _metricsProvider.CacheMiss(methodName);
                if (_options.EnableStatistics)
                {
                    Interlocked.Increment(ref _misses);
                }

                return result!;
            }, methodName, entryPolicy).ConfigureAwait(false);
        }

        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            if (TryGetFastInternal(cacheKey, out T? value))
            {
                return new ValueTask<T?>(value);
            }
            return new ValueTask<T?>((T?)default);
        }

        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            return TryGetFastInternal(cacheKey, out value);
        }

        private bool TryGetFastInternal<T>(string cacheKey, out T? value)
        {
            value = default;

            if (!_options.EnableFastPath)
            {
                return false;
            }

            if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
            {
                if (_options.FastPathTrackMetrics && _options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }

                try
                {
                    value = (T)entry.Value;
                    return true;
                }
                catch (InvalidCastException)
                {
                    value = default;
                }
            }

            return false;
        }

        /// <summary>
        /// Lightweight coordination to prevent duplicate work using Lazy&lt;Task&lt;object&gt;&gt; pattern.
        /// This eliminates TaskCompletionSource allocations and while-loop churn under stampede scenarios.
        /// Waiters are treated as cache hits for accurate metrics.
        /// </summary>
        private async Task<T> ExecuteWithLightweightCoordination<T>(string key, Func<Task<object>> factory)
        {
            var newGate = new Lazy<Task<object>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
            var gate = _lightweightGates.GetOrAdd(key, newGate);
            var isCoordinator = gate == newGate;

            try
            {
                var task = gate.Value;
                if (task.IsCompletedSuccessfully)
                {
                    return (T)task.Result;
                }

                var result = await task.ConfigureAwait(false);
                return (T)result;
            }
            finally
            {
                if (isCoordinator || gate.Value.IsCompleted)
                {
                    _lightweightGates.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, gate));
                }
            }
        }

        /// <summary>
        /// Executes a factory function with single-flight pattern to prevent duplicate concurrent executions for the same key.
        /// Multiple concurrent requests for the same key will wait for the single execution to complete.
        /// </summary>
        private async Task<T> ExecuteWithSingleFlight<T>(string key, Func<Task<T>> factory, string methodName, CacheEntryPolicy policy)
        {
            using var lease = await AcquireDistributedSemaphoreAsync(key, policy).ConfigureAwait(false);

            TaskCompletionSource<object>? tcs = null;
            var isFirstRequest = false;

            try
            {
                // Try to add a new TaskCompletionSource for this key
                tcs = _singleFlightGates.GetOrAdd(key, _ => 
                {
                    isFirstRequest = true;
                    return new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                });
                
                if (isFirstRequest)
                {
                    // This is the first request - execute the factory
                    try
                    {
                        var result = await factory().ConfigureAwait(false);
                        tcs.SetResult(result!);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        _metricsProvider.CacheError(methodName, ex.Message);
                        tcs.SetException(ex);
                        throw;
                    }
                    finally
                    {
                        // Clean up the gate after completion (success or failure)
                        _singleFlightGates.TryRemove(key, out _);
                    }
                }
                else
                {
                    // This is a duplicate request - wait for the original to complete
                    var result = await tcs.Task.ConfigureAwait(false);
                    
                    // Update statistics for the duplicate request
                    _metricsProvider.CacheMiss(methodName);
                    if (_options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                    
                    return (T)result;
                }
            }
            catch (Exception ex) when (!isFirstRequest)
            {
                // For duplicate requests, wrap in a more specific exception
                _metricsProvider.CacheError(methodName, $"Single-flight execution failed: {ex.Message}");
                throw;
            }
        }

        #endregion

        private static CacheEntryPolicy CreatePolicyFromRuntimePolicy(CacheRuntimePolicy policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var stampedeProtection = policy.StampedeProtection != null
                ? new StampedeProtectionOptions(
                    policy.StampedeProtection.Mode,
                    policy.StampedeProtection.Beta,
                    policy.StampedeProtection.RefreshAheadWindow)
                : null;

            var distributedLock = policy.DistributedLock != null
                ? new DistributedLockOptions(
                    policy.DistributedLock.Timeout,
                    policy.DistributedLock.MaxConcurrency)
                : null;

            return new CacheEntryPolicy(
                policy.Duration,
                policy.SlidingExpiration,
                policy.RefreshAhead,
                stampedeProtection,
                distributedLock,
                null); // Metrics are extracted from metadata if needed
        }

        private static bool RequiresSingleFlight(CacheEntryPolicy policy)
        {
            // Fast path: Empty or minimal policy doesn't need heavy single-flight coordination
            if (policy == CacheEntryPolicy.Empty)
            {
                return false;
            }

            // Check for minimal policy (only duration, no advanced features)
            var hasOnlyDuration = policy.Duration.HasValue &&
                                  !policy.SlidingExpiration.HasValue &&
                                  !policy.RefreshAhead.HasValue &&
                                  policy.StampedeProtection == null &&
                                  policy.DistributedLock == null;

            if (hasOnlyDuration)
            {
                return false; // Use lightweight coordination instead
            }

            if (policy.DistributedLock != null)
            {
                return true;
            }

            var stampede = policy.StampedeProtection;
            if (stampede == null)
            {
                return false;
            }

            return stampede.Mode switch
            {
                StampedeProtectionMode.DistributedLock => true,
                StampedeProtectionMode.RefreshAhead => true,
                StampedeProtectionMode.Probabilistic => true,
                _ => false
            };
        }

        #region IMemoryCache Implementation

        public ValueTask<T?> GetAsync<T>(string key)
        {
            return GetAsyncInternal<T>(key, updateStatistics: true);
        }
        
        private ValueTask<T?> GetAsyncInternal<T>(string key, bool updateStatistics)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                // FAST PATH: Check expiration first (like FusionCache)
                if (entry.IsExpired)
                {
                    RemoveEntryCompletely(key, entry);
                    if (updateStatistics && _options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                    }
                    return new ValueTask<T?>(default(T));
                }

                // Check if we need advanced features (refresh-ahead, sliding expiration, etc.)
                var needsAdvancedFeatures =
                    entry.Policy.SlidingExpiration.HasValue ||
                    entry.Policy.RefreshAhead.HasValue ||
                    entry.Policy.StampedeProtection != null;

                if (needsAdvancedFeatures)
                {
                    // SLOW PATH: Full feature set
                    if (ShouldForceRefresh(entry))
                    {
                        RemoveEntryCompletely(key, entry);
                        if (updateStatistics && _options.EnableStatistics)
                        {
                            Interlocked.Increment(ref _misses);
                        }
                        return new ValueTask<T?>(default(T));
                    }

                    ApplySlidingExpiration(entry);
                    entry.UpdateAccess();
                }
                else if (_options.EvictionPolicy == MemoryCacheEvictionPolicy.LRU ||
                         _options.EvictionPolicy == MemoryCacheEvictionPolicy.LFU ||
                         _options.EvictionPolicy == MemoryCacheEvictionPolicy.LFU_Precise)
                {
                    // Only update access for LRU/LFU policies when no advanced features
                    entry.UpdateAccess();
                }
                // FAST PATH: Skip UpdateAccess for FIFO/TTL policies with no advanced features

                if (updateStatistics && _options.EnableStatistics)
                {
                    Interlocked.Increment(ref _hits);
                }

                try
                {
                    return new ValueTask<T?>((T)entry.Value);
                }
                catch (InvalidCastException)
                {
                    // Type mismatch - treat as miss but don't double count
                    if (updateStatistics && _options.EnableStatistics)
                    {
                        Interlocked.Increment(ref _misses);
                        // Compensate for the hit we incorrectly incremented
                        Interlocked.Decrement(ref _hits);
                    }
                    return new ValueTask<T?>(default(T));
                }
            }

            if (updateStatistics && _options.EnableStatistics)
            {
                Interlocked.Increment(ref _misses);
            }
            return new ValueTask<T?>(default(T));
        }

        private static void ApplySlidingExpiration(EnhancedCacheEntry entry)
        {
            if (entry.Policy.SlidingExpiration is TimeSpan sliding && sliding > TimeSpan.Zero)
            {
                entry.AbsoluteExpiration = DateTimeOffset.UtcNow.Add(sliding);
            }
        }

        private bool ShouldForceRefresh(EnhancedCacheEntry entry)
        {
            var now = DateTimeOffset.UtcNow;

            if (entry.Policy.RefreshAhead is TimeSpan refreshAhead && refreshAhead > TimeSpan.Zero)
            {
                var remaining = entry.AbsoluteExpiration - now;
                if (remaining <= refreshAhead)
                {
                    return true;
                }
            }

            var stampede = entry.Policy.StampedeProtection;
            if (stampede == null)
            {
                return false;
            }

            switch (stampede.Mode)
            {
                case StampedeProtectionMode.RefreshAhead:
                    if (stampede.RefreshAheadWindow is TimeSpan stampedeWindow && stampedeWindow > TimeSpan.Zero)
                    {
                        var remaining = entry.AbsoluteExpiration - now;
                        if (remaining <= stampedeWindow)
                        {
                            return true;
                        }
                    }
                    break;
                case StampedeProtectionMode.Probabilistic:
                {
                    var duration = entry.Policy.Duration ?? _options.DefaultExpiration;
                    if (duration <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    var age = now - entry.CreatedAt;
                    if (age <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    var beta = stampede.Beta <= 0 ? 1d : stampede.Beta;
                    var ratio = Math.Min(1d, age.TotalSeconds / duration.TotalSeconds);
                    var probability = Math.Exp(-beta * ratio);
                    var sample = Random.Shared.NextDouble();
                    return sample > probability;
                }
                case StampedeProtectionMode.DistributedLock:
                case StampedeProtectionMode.None:
                default:
                    break;
            }

            return false;
        }

        private async Task<SemaphoreReleaser> AcquireDistributedSemaphoreAsync(string key, CacheEntryPolicy policy)
        {
            var requiresLock = policy.DistributedLock != null || policy.StampedeProtection?.Mode == StampedeProtectionMode.DistributedLock;
            if (!requiresLock)
            {
                return SemaphoreReleaser.None;
            }

            var options = policy.DistributedLock ?? new DistributedLockOptions(TimeSpan.FromSeconds(30), 1);
            var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(30);
            var maxConcurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency : 1;

            var state = _distributedLocks.GetOrAdd(key, _ => new DistributedLockState(new SemaphoreSlim(maxConcurrency, maxConcurrency), maxConcurrency));

            if (!await state.Semaphore.WaitAsync(timeout).ConfigureAwait(false))
            {
                throw new TimeoutException($"Timed out acquiring distributed lock for key '{key}'.");
            }

            return new SemaphoreReleaser(state.Semaphore);
        }

        private static Regex WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        private sealed class SemaphoreReleaser : IDisposable
        {
            public static readonly SemaphoreReleaser None = new(null);

            private readonly SemaphoreSlim? _semaphore;

            public SemaphoreReleaser(SemaphoreSlim? semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                _semaphore?.Release();
            }
        }
        
        // UpdateAccessOrder methods removed - using lazy LRU (timestamp-based) instead
        // Timestamps are updated in UpdateAccess(), sorting happens only at eviction time
        
        /// <summary>
        /// Completely removes an entry from all data structures.
        /// </summary>
        private void RemoveEntryCompletely(string key, EnhancedCacheEntry entry)
        {
            _cache.TryRemove(key, out _);

            // Remove from tag index
            RemoveFromTagIndex(key, entry.Tags);

            _distributedLocks.TryRemove(key, out _);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan expiration)
        {
            await SetAsync(key, value, expiration, Array.Empty<string>());
        }

        private async Task SetAsync<T>(string key, T value, TimeSpan expiration, string[] tags, CacheEntryPolicy? policy = null)
        {
            if (value == null)
            {
                await RemoveAsync(key);
                return;
            }

            var effectiveExpiration = expiration > _options.MaxExpiration
                ? _options.MaxExpiration
                : expiration;

            // FAST PATH: No tags, under capacity, no complex features
            // This path avoids HashSet allocation, tag index locking, and eviction checks
            var hasTags = tags.Length > 0;
            var hasComplexPolicy = policy != null && policy != CacheEntryPolicy.Empty &&
                                   (policy.SlidingExpiration.HasValue ||
                                    policy.RefreshAhead.HasValue ||
                                    policy.StampedeProtection != null ||
                                    policy.DistributedLock != null);

            var currentCount = _cache.Count;
            var capacityThreshold = (int)(_options.MaxItems * 0.9); // 90% capacity

            if (!hasTags && !hasComplexPolicy && currentCount < capacityThreshold)
            {
                // Ultra-fast path: minimal allocations, no locks, no eviction
                var entry = new EnhancedCacheEntry
                {
                    Value = value!,
                    Tags = null, // No HashSet allocation
                    CreatedAt = DateTime.UtcNow,
                    AbsoluteExpiration = DateTimeOffset.UtcNow.Add(effectiveExpiration),
                    LastAccessedAt = DateTime.UtcNow,
                    Policy = CacheEntryPolicy.Empty
                };

                // Check if updating existing entry
                if (_cache.TryGetValue(key, out var oldEntry))
                {
                    // Only remove from tag index if old entry had tags
                    if (oldEntry.Tags != null && oldEntry.Tags.Count > 0)
                    {
                        RemoveFromTagIndex(key, oldEntry.Tags);
                    }
                }

                // Simple dictionary insert - lock-free for ConcurrentDictionary
                _cache[key] = entry;
                return; // Skip tag index, skip eviction semaphore
            }

            // SLOW PATH: Complex scenarios with tags, eviction, or advanced features
            var tagSet = hasTags ? new HashSet<string>(tags) : null;

            var fullEntry = new EnhancedCacheEntry
            {
                Value = value!,
                Tags = tagSet,
                CreatedAt = DateTime.UtcNow,
                AbsoluteExpiration = DateTimeOffset.UtcNow.Add(effectiveExpiration),
                LastAccessedAt = DateTime.UtcNow,
                Policy = policy ?? CacheEntryPolicy.Empty
            };

            // Check if this is an update to existing entry
            var isUpdate = _cache.TryGetValue(key, out var existingEntry);

            // Only evict if this is a new entry and we're at capacity
            if (!isUpdate && _cache.Count >= _options.MaxItems)
            {
                await TryEvictAsync();
            }

            // Remove old entry completely if updating
            if (isUpdate && existingEntry != null)
            {
                RemoveEntryCompletely(key, existingEntry);
            }

            // Add the new entry to all data structures
            _cache[key] = fullEntry;
            AddToTagIndex(key, fullEntry.Tags);
        }
        
        /// <summary>
        /// Adds entry to tag reverse index with size limit protection.
        /// Lock-free implementation using CAS operations for high concurrency.
        /// </summary>
        private void TryScheduleTagCleanup()
        {
            if (Interlocked.CompareExchange(ref _cleanupScheduled, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CleanupStaleTagsAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _metricsProvider.CacheError(nameof(InMemoryCacheManager), $"Tag cleanup failed: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _cleanupScheduled, 0);
                    }
                });
            }
        }

        private void AddToTagIndex(string key, HashSet<string>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return;
            }

            // Check if we're approaching the limit (lock-free read)
            if (_currentTagMappings >= MaxTagMappings)
            {
                TryScheduleTagCleanup();
            }

            // Lock-free tag additions using ConcurrentDictionary
            foreach (var tag in tags)
            {
                var tagKeys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
                if (tagKeys.TryAdd(key, 0))
                {
                    Interlocked.Increment(ref _currentTagMappings);
                }
            }
        }
        
        /// <summary>
        /// Removes entry from tag reverse index.
        /// Lock-free implementation using ConcurrentDictionary operations.
        /// </summary>
        private void RemoveFromTagIndex(string key, HashSet<string>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return;
            }

            // Lock-free tag removals
            foreach (var tag in tags)
            {
                if (_tagToKeys.TryGetValue(tag, out var tagKeys))
                {
                    if (tagKeys.TryRemove(key, out _))
                    {
                        Interlocked.Decrement(ref _currentTagMappings);
                    }
                    // Clean up empty tag entries
                    if (tagKeys.IsEmpty)
                    {
                        _tagToKeys.TryRemove(tag, out _);
                    }
                }
            }
        }
        
        /// <summary>
        /// Cleanup stale tag mappings to prevent memory leaks.
        /// Lock-free async implementation that runs in background.
        /// </summary>
        private async Task CleanupStaleTagsAsync()
        {
            // Yield to ensure we're running on thread pool
            await Task.Yield();

            var keysToRemove = new List<string>();

            // Check all tag mappings and remove entries that no longer exist in cache
            foreach (var tagEntry in _tagToKeys)
            {
                foreach (var key in tagEntry.Value.Keys)
                {
                    if (!_cache.ContainsKey(key))
                    {
                        keysToRemove.Add(key);
                    }
                }

                // Remove stale keys from this tag (lock-free)
                foreach (var key in keysToRemove)
                {
                    if (tagEntry.Value.TryRemove(key, out _))
                    {
                        Interlocked.Decrement(ref _currentTagMappings);
                    }
                }

                keysToRemove.Clear();

                // Remove empty tag entries (lock-free)
                if (tagEntry.Value.IsEmpty)
                {
                    _tagToKeys.TryRemove(tagEntry.Key, out _);
                }
            }
        }

        public ValueTask<bool> RemoveAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                RemoveEntryCompletely(key, entry);
                return new ValueTask<bool>(true);
            }
            return new ValueTask<bool>(false);
        }

        public ValueTask ClearAsync()
        {
            _cache.Clear();
            _singleFlightGates.Clear();
            _lightweightGates.Clear();

            // Lock-free clear - ConcurrentDictionary.Clear() is thread-safe
            _tagToKeys.Clear();
            Interlocked.Exchange(ref _currentTagMappings, 0);

            // Reset statistics
            if (_options.EnableStatistics)
            {
                Interlocked.Exchange(ref _hits, 0);
                Interlocked.Exchange(ref _misses, 0);
                Interlocked.Exchange(ref _evictions, 0);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ICacheStats> GetStatsAsync()
        {
            var stats = new CacheStats
            {
                Hits = _hits,
                Misses = _misses,
                Evictions = _evictions,
                Entries = _cache.Count,
                MemoryUsage = EstimateMemoryUsage()
            };

            return new ValueTask<ICacheStats>(stats);
        }

        public ValueTask<int> RemoveMultipleAsync(params string[] keys)
        {
            var removed = 0;
            foreach (var key in keys)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    removed++;
                }
            }
            
            return new ValueTask<int>(removed);
        }

        public ValueTask<bool> ExistsAsync(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired)
                {
                    RemoveEntryCompletely(key, entry);
                    return new ValueTask<bool>(false);
                }
                return new ValueTask<bool>(true);
            }
            return new ValueTask<bool>(false);
        }

        #endregion

        #region Advanced Features

        private async Task TryEvictAsync()
        {
            if (!await _evictionSemaphore.WaitAsync(100)) // Don't wait long for eviction lock
                return;

            try
            {
                var currentCount = _cache.Count;
                if (currentCount <= _options.MaxItems)
                {
                    return; // No eviction needed
                }
                
                // Calculate how many entries to evict
                var targetCount = (int)(_options.MaxItems * 0.9); // Target 90% capacity
                var evictCount = Math.Max(1, currentCount - targetCount);
                evictCount = Math.Min(evictCount, (int)(_options.MaxItems * 0.2)); // Never evict more than 20%
                
                int actualEvicted = 0;
                
                // Use appropriate eviction algorithm based on policy
                switch (_options.EvictionPolicy)
                {
                    case MemoryCacheEvictionPolicy.LRU:
                    case MemoryCacheEvictionPolicy.FIFO:
                        actualEvicted = EvictFromAccessOrderO1(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.LFU:
                        actualEvicted = EvictLFUApproximate(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.LFU_Precise:
                        actualEvicted = EvictLFUPrecise(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.TTL:
                        actualEvicted = EvictTTLApproximate(evictCount);
                        break;
                        
                    case MemoryCacheEvictionPolicy.TTL_Precise:
                        actualEvicted = EvictTTLPrecise(evictCount);
                        break;
                        
                    default:
                        actualEvicted = EvictFromAccessOrderO1(evictCount);
                        break;
                }
                
                if (_options.EnableStatistics)
                {
                    Interlocked.Add(ref _evictions, actualEvicted);
                }
            }
            finally
            {
                _evictionSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Lazy LRU/FIFO eviction using timestamp-based sorting (like Microsoft.Extensions.Caching.Memory).
        /// No locks during cache hits, sorting only happens here during eviction.
        /// </summary>
        private int EvictFromAccessOrderO1(int maxEvictions)
        {
            var totalItems = _cache.Count;
            if (totalItems == 0 || maxEvictions <= 0)
            {
                return 0;
            }

            var sampleSize = Math.Max(maxEvictions, (int)(totalItems * _options.EvictionSamplePercentage));
            sampleSize = Math.Min(sampleSize, totalItems);

            var candidates = sampleSize >= totalItems
                ? _cache.ToList()
                : TakeRandomSample(sampleSize);

            var ordered = (_options.EvictionPolicy == MemoryCacheEvictionPolicy.LRU
                    ? candidates.OrderBy(kvp => kvp.Value.LastAccessedAt)
                    : candidates.OrderBy(kvp => kvp.Value.CreatedAt))
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();

            var evicted = 0;
            foreach (var key in ordered)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }

            return evicted;
        }
        
        /// <summary>
        /// Approximate LFU eviction using sampling for better performance.
        /// Trades precision for speed - may not evict the globally least frequently used item.
        /// </summary>
        private int EvictLFUApproximate(int maxEvictions)
        {
            var totalItems = _cache.Count;
            var sampleSize = Math.Max(maxEvictions, (int)(totalItems * _options.EvictionSamplePercentage));
            sampleSize = Math.Min(sampleSize, totalItems);
            
            var candidates = _cache.Take(sampleSize)
                .OrderBy(kvp => kvp.Value.AccessCount)
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Precise LFU eviction - guarantees eviction of globally least frequently used items.
        /// WARNING: O(N log N) performance - expensive for large caches.
        /// </summary>
        private int EvictLFUPrecise(int maxEvictions)
        {
            var candidates = _cache
                .OrderBy(kvp => kvp.Value.AccessCount)
                .ThenBy(kvp => kvp.Value.LastAccessedAt) // Tiebreaker: older access wins
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Approximate TTL eviction using sampling for better performance.
        /// Trades precision for speed - may not evict the item globally closest to expiration.
        /// </summary>
        private int EvictTTLApproximate(int maxEvictions)
        {
            var totalItems = _cache.Count;
            var sampleSize = Math.Max(maxEvictions, (int)(totalItems * _options.EvictionSamplePercentage));
            sampleSize = Math.Min(sampleSize, totalItems);
            
            var candidates = _cache.Take(sampleSize)
                .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }
            
            return evicted;
        }
        
        /// <summary>
        /// Precise TTL eviction - guarantees eviction of items globally closest to expiration.
        /// WARNING: O(N log N) performance - expensive for large caches.
        /// </summary>
        private int EvictTTLPrecise(int maxEvictions)
        {
            var candidates = _cache
                .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
                .ThenBy(kvp => kvp.Value.CreatedAt) // Tiebreaker: older creation wins
                .Take(maxEvictions)
                .Select(kvp => kvp.Key)
                .ToList();
            
            int evicted = 0;
            foreach (var key in candidates)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    RemoveEntryCompletely(key, entry);
                    evicted++;
                }
            }

            return evicted;
        }

        private List<KeyValuePair<string, EnhancedCacheEntry>> TakeRandomSample(int sampleSize)
        {
            var reservoir = new List<KeyValuePair<string, EnhancedCacheEntry>>(sampleSize);
            var count = 0;

            foreach (var kvp in _cache)
            {
                if (count < sampleSize)
                {
                    reservoir.Add(kvp);
                }
                else
                {
                    var index = Random.Shared.Next(count + 1);
                    if (index < sampleSize)
                    {
                        reservoir[index] = kvp;
                    }
                }

                count++;
            }

            return reservoir;
        }


        private void CleanupExpiredEntries(object? state)
        {
            try
            {
                var expiredKeys = new List<string>();
                const int maxBatchSize = 1000; // Process in batches to avoid long pauses
                int processedCount = 0;
                
                // Use sampling approach for large caches instead of full enumeration
                var totalCount = _cache.Count;
                var sampleSize = Math.Min(totalCount, maxBatchSize);
                
                if (totalCount <= maxBatchSize)
                {
                    // Small cache - check all entries
                    foreach (var kvp in _cache)
                    {
                        if (kvp.Value.IsExpired)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                        
                        if (++processedCount >= maxBatchSize)
                            break;
                    }
                }
                else
                {
                    // Large cache - use sampling to avoid performance impact
                    var sample = _cache.Take(sampleSize);
                    foreach (var kvp in sample)
                    {
                        if (kvp.Value.IsExpired)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                    }
                }

                // Use proper removal method that cleans up all data structures
                foreach (var key in expiredKeys)
                {
                    if (_cache.TryGetValue(key, out var entry) && entry.IsExpired)
                    {
                        RemoveEntryCompletely(key, entry);
                    }
                }
                
                // If we found many expired entries in our sample, schedule another cleanup soon
                if (expiredKeys.Count > sampleSize * 0.5 && totalCount > maxBatchSize)
                {
                    // High expiration rate detected - schedule more frequent cleanup
                    _cleanupTimer?.Change(TimeSpan.FromSeconds(10), _options.CleanupInterval);
                }
            }
            catch (Exception ex)
            {
                // Log the exception if possible, but don't crash the application
                // In a real implementation, we'd want to log this
                System.Diagnostics.Debug.WriteLine($"Error in background cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Estimates memory usage using the configured calculation mode.
        /// </summary>
        private long EstimateMemoryUsage()
        {
            return _memoryCalculator.CalculateMemoryUsage(_cache, entry => entry.Value);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _evictionSemaphore?.Dispose();
                _cache.Clear();
                _singleFlightGates.Clear();
                _disposed = true;
            }
        }

        #endregion
    }
}
