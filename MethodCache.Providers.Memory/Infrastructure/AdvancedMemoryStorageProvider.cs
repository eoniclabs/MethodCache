using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Memory.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.Providers.Memory.Infrastructure;

/// <summary>
/// Advanced memory storage provider with sophisticated eviction policies and statistics.
/// Implements IStorageProvider for distributed cache scenarios while providing L2-like semantics in memory.
/// </summary>
public class AdvancedMemoryStorageProvider : IStorageProvider, IAsyncDisposable, IDisposable
{
    private class CacheEntry
    {
        private long _accessCount;

        public required object Value { get; init; }
        public required HashSet<string> Tags { get; init; }
        public required DateTimeOffset AbsoluteExpiration { get; set; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastAccessedAt { get; set; }
        public long AccessCount => _accessCount;
        public LinkedListNode<string>? OrderNode { get; set; }

        public bool IsExpired => DateTime.UtcNow > AbsoluteExpiration;

        public void UpdateAccess()
        {
            LastAccessedAt = DateTime.UtcNow;
            Interlocked.Increment(ref _accessCount);
        }

        public long EstimateSize(MemoryUsageCalculationMode mode)
        {
            if (mode == MemoryUsageCalculationMode.Estimated)
            {
                // Fast estimation
                const int baseSize = 64; // Base object overhead
                var tagsSize = Tags.Count * 20; // Approximate string overhead
                var valueSize = Value switch
                {
                    string s => s.Length * 2, // Unicode chars
                    byte[] b => b.Length,
                    _ => 64 // Rough estimate for other objects
                };
                return baseSize + tagsSize + valueSize;
            }
            else
            {
                // More accurate calculation with overhead
                const int objectOverhead = 24; // Object header in .NET
                const int referenceSize = 8; // 64-bit reference
                const int dateTimeSize = 16;
                const int hashSetOverhead = 48;

                long size = objectOverhead; // CacheEntry itself
                size += referenceSize; // Value reference
                size += hashSetOverhead + (Tags.Count * 32); // Tags with string overhead
                size += dateTimeSize * 3; // AbsoluteExpiration, CreatedAt, LastAccessedAt
                size += 8; // AccessCount
                size += referenceSize; // OrderNode reference

                // Value size
                size += Value switch
                {
                    string s => 26 + (s.Length * 2), // String overhead + chars
                    byte[] b => 24 + b.Length, // Array overhead + data
                    Array a => 24 + (a.Length * 8), // Array overhead + elements
                    _ => 64 // Conservative estimate
                };

                return size;
            }
        }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new();
    private readonly LinkedList<string> _accessOrder = new();
    private readonly object _accessOrderLock = new();
    private readonly object _tagIndexLock = new();
    private readonly Timer? _cleanupTimer;
    private readonly AdvancedMemoryOptions _options;
    private readonly ILogger<AdvancedMemoryStorageProvider> _logger;
    private readonly ThreadLocal<Random> _threadLocalRandom = new(() => new Random(Guid.NewGuid().GetHashCode()));

    // Statistics
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _estimatedMemoryUsage;
    private long _currentTagMappings;
    private bool _disposed;

    public string Name => "AdvancedMemory";

    public AdvancedMemoryStorageProvider(
        IOptions<AdvancedMemoryOptions> options,
        ILogger<AdvancedMemoryStorageProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.EnableAutomaticCleanup)
        {
            _cleanupTimer = new Timer(CleanupExpiredEntries, null, _options.CleanupInterval, _options.CleanupInterval);
        }

        _logger.LogInformation("AdvancedMemoryStorageProvider initialized with policy {Policy}, max entries {MaxEntries}",
            _options.EvictionPolicy, _options.MaxEntries);
    }

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                RemoveFromTagIndex(key, entry.Tags);
                RemoveFromAccessOrder(entry);
                Interlocked.Increment(ref _misses);
                return ValueTask.FromResult<T?>(default);
            }

            entry.UpdateAccess();
            UpdateAccessOrder(key, entry, remove: false);
            Interlocked.Increment(ref _hits);

            if (entry.Value is T typedValue)
            {
                return ValueTask.FromResult<T?>(typedValue);
            }

            _logger.LogWarning("Cache entry for key {Key} is not of expected type {Type}", key, typeof(T));
            return ValueTask.FromResult<T?>(default);
        }

        Interlocked.Increment(ref _misses);
        return ValueTask.FromResult<T?>(default);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        return SetAsync(key, value, expiration, Array.Empty<string>(), cancellationToken);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (value == null) return;

        var tagSet = new HashSet<string>(tags);
        var absoluteExpiration = DateTimeOffset.UtcNow.Add(expiration);

        var entry = new CacheEntry
        {
            Value = value,
            Tags = tagSet,
            AbsoluteExpiration = absoluteExpiration,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        var estimatedSize = entry.EstimateSize(_options.MemoryCalculationMode);

        // Add memory BEFORE eviction check so decisions are accurate
        Interlocked.Add(ref _estimatedMemoryUsage, estimatedSize);

        // Check if we need to evict entries before adding
        await CheckAndEvictIfNeeded(key, entry).ConfigureAwait(false);

        _cache.AddOrUpdate(key, entry, (k, existing) =>
        {
            // Remove old tag mappings
            RemoveFromTagIndex(k, existing.Tags);
            // Remove from access order while we still have the entry
            RemoveFromAccessOrder(existing);
            // Subtract the old entry's size from memory usage
            Interlocked.Add(ref _estimatedMemoryUsage, -existing.EstimateSize(_options.MemoryCalculationMode));
            return entry;
        });

        // Add to tag index
        AddToTagIndex(key, tagSet);

        // Update access order atomically with the entry we just added
        lock (_accessOrderLock)
        {
            // Remove from current position if it exists
            if (entry.OrderNode != null)
            {
                _accessOrder.Remove(entry.OrderNode);
            }
            // Add to end (most recently used)
            entry.OrderNode = _accessOrder.AddLast(key);
        }

        _logger.LogDebug("Set cache entry {Key} with expiration {Expiration} and tags {Tags}",
            key, expiration, string.Join(", ", tagSet));
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            RemoveFromTagIndex(key, entry.Tags);
            // Remove from access order using the entry we still have
            RemoveFromAccessOrder(entry);
            Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize(_options.MemoryCalculationMode));

            _logger.LogDebug("Removed cache entry {Key}", key);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (_tagToKeys.TryGetValue(tag, out var keys))
        {
            var keysToRemove = keys.Keys.ToList();

            foreach (var key in keysToRemove)
            {
                await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug("Removed {Count} cache entries with tag {Tag}", keysToRemove.Count, tag);
        }
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                RemoveFromTagIndex(key, entry.Tags);
                RemoveFromAccessOrder(entry);
                return ValueTask.FromResult(false);
            }
            return ValueTask.FromResult(true);
        }
        return ValueTask.FromResult(false);
    }

    public ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entryCount = _cache.Count;
            var memoryUsage = Interlocked.Read(ref _estimatedMemoryUsage);
            var hitRatio = GetHitRatio();

            // Health checks
            var isHealthy = entryCount < _options.MaxEntries * 0.9 && // Not near capacity
                           memoryUsage < _options.MaxMemoryUsage * 0.9 && // Not near memory limit
                           hitRatio > 0.1; // Reasonable hit ratio

            var status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded;

            return ValueTask.FromResult(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for AdvancedMemoryStorageProvider");
            return ValueTask.FromResult(HealthStatus.Unhealthy);
        }
    }

    public ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new StorageStats
        {
            GetOperations = Interlocked.Read(ref _hits) + Interlocked.Read(ref _misses),
            SetOperations = _cache.Count,
            RemoveOperations = Interlocked.Read(ref _evictions),
            AverageResponseTimeMs = 0.1, // Memory operations are very fast
            ErrorCount = 0,
            AdditionalStats = new Dictionary<string, object>
            {
                ["EntryCount"] = _cache.Count,
                ["Hits"] = Interlocked.Read(ref _hits),
                ["Misses"] = Interlocked.Read(ref _misses),
                ["Evictions"] = Interlocked.Read(ref _evictions),
                ["EstimatedMemoryUsage"] = Interlocked.Read(ref _estimatedMemoryUsage),
                ["HitRatio"] = GetHitRatio(),
                ["TagMappingCount"] = _currentTagMappings,
                ["EvictionPolicy"] = _options.EvictionPolicy.ToString(),
                ["MaxEntries"] = _options.MaxEntries,
                ["MaxMemoryUsage"] = _options.MaxMemoryUsage
            }
        };

        return ValueTask.FromResult<StorageStats?>(stats);
    }

    private async ValueTask CheckAndEvictIfNeeded(string newKey, CacheEntry newEntry)
    {
        var currentCount = _cache.Count;
        var estimatedSize = newEntry.EstimateSize(_options.MemoryCalculationMode);
        var currentMemory = Interlocked.Read(ref _estimatedMemoryUsage);

        if (currentCount >= _options.MaxEntries || currentMemory + estimatedSize > _options.MaxMemoryUsage)
        {
            await EvictEntries(1).ConfigureAwait(false); // Evict at least one entry
        }

        Interlocked.Add(ref _estimatedMemoryUsage, estimatedSize);
    }

    private ValueTask EvictEntries(int minToEvict)
    {
        var evicted = 0;
        var candidates = GetEvictionCandidates();

        foreach (var key in candidates)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                RemoveFromTagIndex(key, entry.Tags);
                RemoveFromAccessOrder(entry);
                Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize(_options.MemoryCalculationMode));
                Interlocked.Increment(ref _evictions);
                evicted++;

                if (evicted >= minToEvict)
                    break;
            }
        }

        if (evicted > 0)
        {
            _logger.LogDebug("Evicted {Count} entries using {Policy} policy", evicted, _options.EvictionPolicy);
        }
        return ValueTask.CompletedTask;
    }

    private IEnumerable<string> GetEvictionCandidates()
    {
        return _options.EvictionPolicy switch
        {
            EvictionPolicy.LRU => GetLRUCandidates(),
            EvictionPolicy.LFU => GetLFUCandidates(),
            EvictionPolicy.TTL => GetTTLCandidates(),
            EvictionPolicy.Random => GetRandomCandidates(),
            _ => GetLRUCandidates()
        };
    }

    private IEnumerable<string> GetLRUCandidates()
    {
        lock (_accessOrderLock)
        {
            // Return an iterator instead of copying the entire list
            // This yields items one at a time as needed
            foreach (var key in _accessOrder)
            {
                yield return key;
            }
        }
    }

    private IEnumerable<string> GetLFUCandidates()
    {
        // Use a min-heap approach with limited sampling for O(n) instead of O(n log n)
        const int sampleSize = 100;
        var candidates = new List<(string key, long accessCount)>(sampleSize);

        foreach (var kvp in _cache)
        {
            if (candidates.Count < sampleSize)
            {
                candidates.Add((kvp.Key, kvp.Value.AccessCount));
                if (candidates.Count == sampleSize)
                {
                    candidates.Sort((a, b) => a.accessCount.CompareTo(b.accessCount));
                }
            }
            else if (kvp.Value.AccessCount < candidates[^1].accessCount)
            {
                // Replace the highest access count with this lower one
                candidates[^1] = (kvp.Key, kvp.Value.AccessCount);
                // Re-sort only if needed (using insertion sort for small changes)
                for (int i = candidates.Count - 2; i >= 0; i--)
                {
                    if (candidates[i].accessCount > candidates[i + 1].accessCount)
                    {
                        (candidates[i], candidates[i + 1]) = (candidates[i + 1], candidates[i]);
                    }
                    else break;
                }
            }
        }

        return candidates.Select(c => c.key);
    }

    private IEnumerable<string> GetTTLCandidates()
    {
        // Use sampling approach similar to LFU
        const int sampleSize = 100;
        var candidates = new List<(string key, DateTimeOffset expiration)>(sampleSize);

        foreach (var kvp in _cache)
        {
            if (candidates.Count < sampleSize)
            {
                candidates.Add((kvp.Key, kvp.Value.AbsoluteExpiration));
                if (candidates.Count == sampleSize)
                {
                    candidates.Sort((a, b) => a.expiration.CompareTo(b.expiration));
                }
            }
            else if (kvp.Value.AbsoluteExpiration < candidates[^1].expiration)
            {
                candidates[^1] = (kvp.Key, kvp.Value.AbsoluteExpiration);
                for (int i = candidates.Count - 2; i >= 0; i--)
                {
                    if (candidates[i].expiration > candidates[i + 1].expiration)
                    {
                        (candidates[i], candidates[i + 1]) = (candidates[i + 1], candidates[i]);
                    }
                    else break;
                }
            }
        }

        return candidates.Select(c => c.key);
    }

    private IEnumerable<string> GetRandomCandidates()
    {
        var random = _threadLocalRandom.Value!;
        var candidatesNeeded = Math.Min(_cache.Count / 4, 100);

        // For small caches, just iterate and yield randomly
        if (_cache.Count <= 100)
        {
            var keys = _cache.Keys.ToList();
            while (keys.Count > 0)
            {
                var index = random.Next(keys.Count);
                yield return keys[index];
                keys.RemoveAt(index);
            }
            yield break;
        }

        // For large caches, use reservoir sampling to avoid full allocation
        var reservoir = new List<string>(candidatesNeeded);
        var itemIndex = 0;

        foreach (var key in _cache.Keys)
        {
            if (reservoir.Count < candidatesNeeded)
            {
                reservoir.Add(key);
            }
            else
            {
                var j = random.Next(itemIndex + 1);
                if (j < candidatesNeeded)
                {
                    reservoir[j] = key;
                }
            }
            itemIndex++;
        }

        foreach (var key in reservoir)
        {
            yield return key;
        }
    }

    private void UpdateAccessOrder(string key, CacheEntry entry, bool remove)
    {
        lock (_accessOrderLock)
        {
            if (remove)
            {
                if (entry.OrderNode != null)
                {
                    _accessOrder.Remove(entry.OrderNode);
                    entry.OrderNode = null;
                }
            }
            else
            {
                // Remove from current position if it exists
                if (entry.OrderNode != null)
                {
                    _accessOrder.Remove(entry.OrderNode);
                }

                // Add to end (most recently used)
                entry.OrderNode = _accessOrder.AddLast(key);
            }
        }
    }

    private void RemoveFromAccessOrder(CacheEntry entry)
    {
        lock (_accessOrderLock)
        {
            if (entry.OrderNode != null)
            {
                _accessOrder.Remove(entry.OrderNode);
                entry.OrderNode = null;
            }
        }
    }

    private void AddToTagIndex(string key, HashSet<string> tags)
    {
        if (Interlocked.Read(ref _currentTagMappings) >= _options.MaxTagMappings)
        {
            _logger.LogWarning("Tag mapping limit reached, skipping tag indexing for key {Key}", key);
            return;
        }

        // Reduce lock contention by doing operations per-tag instead of locking entire loop
        foreach (var tag in tags)
        {
            var keys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
            if (keys.TryAdd(key, 0))
            {
                Interlocked.Increment(ref _currentTagMappings);
            }
        }
    }

    private void RemoveFromTagIndex(string key, HashSet<string> tags)
    {
        // No lock needed - ConcurrentDictionary handles thread safety
        foreach (var tag in tags)
        {
            if (_tagToKeys.TryGetValue(tag, out var keys))
            {
                if (keys.TryRemove(key, out _))
                {
                    Interlocked.Decrement(ref _currentTagMappings);
                }

                if (keys.IsEmpty)
                {
                    _tagToKeys.TryRemove(tag, out _);
                }
            }
        }
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed) return;

        const int batchSize = 100;
        var cleanedCount = 0;

        // Process in batches to avoid allocating large lists
        var batch = new List<string>(batchSize);

        foreach (var kvp in _cache)
        {
            if (kvp.Value.IsExpired)
            {
                batch.Add(kvp.Key);

                if (batch.Count >= batchSize)
                {
                    cleanedCount += RemoveBatch(batch);
                    batch.Clear();
                }
            }
        }

        // Process remaining items
        if (batch.Count > 0)
        {
            cleanedCount += RemoveBatch(batch);
        }

        if (cleanedCount > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired entries", cleanedCount);
        }
    }

    private int RemoveBatch(List<string> keys)
    {
        var removed = 0;
        foreach (var key in keys)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                RemoveFromTagIndex(key, entry.Tags);
                RemoveFromAccessOrder(entry);
                Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize(_options.MemoryCalculationMode));
                removed++;
            }
        }
        return removed;
    }

    private double GetHitRatio()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        return total > 0 ? (double)hits / total : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cleanupTimer?.Dispose();
        _cache.Clear();
        _tagToKeys.Clear();

        lock (_accessOrderLock)
        {
            _accessOrder.Clear();
        }

        _disposed = true;
        _logger.LogInformation("AdvancedMemoryStorageProvider disposed");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear()
    {
        if (_disposed) return;

        _cache.Clear();
        _tagToKeys.Clear();

        lock (_accessOrderLock)
        {
            _accessOrder.Clear();
        }

        // Reset statistics
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _evictions, 0);
        Interlocked.Exchange(ref _estimatedMemoryUsage, 0);
        Interlocked.Exchange(ref _currentTagMappings, 0);

        _logger.LogInformation("AdvancedMemoryStorageProvider cache cleared");
    }
}
