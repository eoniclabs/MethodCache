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

        public long EstimateSize()
        {
            // Simple estimation - in production this could be more sophisticated
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
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new();
    private readonly LinkedList<string> _accessOrder = new();
    private readonly object _accessOrderLock = new();
    private readonly object _tagIndexLock = new();
    private readonly Timer? _cleanupTimer;
    private readonly AdvancedMemoryOptions _options;
    private readonly ILogger<AdvancedMemoryStorageProvider> _logger;
    private static readonly Random _randomInstance = new();

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

        // Check if we need to evict entries before adding
        await CheckAndEvictIfNeeded(key, entry).ConfigureAwait(false);

        _cache.AddOrUpdate(key, entry, (k, existing) =>
        {
            // Remove old tag mappings
            RemoveFromTagIndex(k, existing.Tags);
            // Remove from access order while we still have the entry
            RemoveFromAccessOrder(existing);
            // Subtract the old entry's size from memory usage
            Interlocked.Add(ref _estimatedMemoryUsage, -existing.EstimateSize());
            return entry;
        });

        // Add to tag index
        AddToTagIndex(key, tagSet);

        // Update access order
        UpdateAccessOrder(key, entry, remove: false);


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
            Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize());

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
        var estimatedSize = newEntry.EstimateSize();
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
                Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize());
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
        return _cache
            .OrderBy(kvp => kvp.Value.AccessCount)
            .Select(kvp => kvp.Key);
    }

    private IEnumerable<string> GetTTLCandidates()
    {
        return _cache
            .OrderBy(kvp => kvp.Value.AbsoluteExpiration)
            .Select(kvp => kvp.Key);
    }

    private IEnumerable<string> GetRandomCandidates()
    {
        // Use Fisher-Yates shuffle for efficient random sampling
        // Only shuffle what we need, not the entire collection
        var keys = _cache.Keys.ToArray();
        var count = keys.Length;

        // For small collections, just return all keys in random order
        if (count <= 10)
        {
            for (int i = count - 1; i > 0; i--)
            {
                int j = _randomInstance.Next(i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }
            return keys;
        }

        // For larger collections, only partially shuffle to get enough candidates
        var candidatesNeeded = Math.Min(count / 4, 100); // Take up to 25% or 100 items
        for (int i = 0; i < candidatesNeeded && i < count; i++)
        {
            int j = _randomInstance.Next(i, count);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }

        return keys.Take(candidatesNeeded);
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

        lock (_tagIndexLock)
        {
            foreach (var tag in tags)
            {
                var keys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
                if (keys.TryAdd(key, 0))
                {
                    Interlocked.Increment(ref _currentTagMappings);
                }
            }
        }
    }

    private void RemoveFromTagIndex(string key, HashSet<string> tags)
    {
        lock (_tagIndexLock)
        {
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
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed) return;

        var expiredKeys = _cache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                RemoveFromTagIndex(key, entry.Tags);
                RemoveFromAccessOrder(entry);
                Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize());
            }
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired entries", expiredKeys.Count);
        }
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
