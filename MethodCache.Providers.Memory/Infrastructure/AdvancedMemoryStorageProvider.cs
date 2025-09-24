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

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                RemoveFromTagIndex(key, entry.Tags);
                UpdateAccessOrder(key, remove: true);
                Interlocked.Increment(ref _misses);
                return Task.FromResult<T?>(default);
            }

            entry.UpdateAccess();
            UpdateAccessOrder(key, remove: false);
            Interlocked.Increment(ref _hits);

            if (entry.Value is T typedValue)
            {
                return Task.FromResult<T?>(typedValue);
            }

            _logger.LogWarning("Cache entry for key {Key} is not of expected type {Type}", key, typeof(T));
            return Task.FromResult<T?>(default);
        }

        Interlocked.Increment(ref _misses);
        return Task.FromResult<T?>(default);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        await SetAsync(key, value, expiration, Array.Empty<string>(), cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
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
        await CheckAndEvictIfNeeded(key, entry);

        _cache.AddOrUpdate(key, entry, (k, existing) =>
        {
            // Remove old tag mappings
            RemoveFromTagIndex(k, existing.Tags);
            UpdateAccessOrder(k, remove: true);
            return entry;
        });

        // Add to tag index
        AddToTagIndex(key, tagSet);

        // Update access order
        UpdateAccessOrder(key, remove: false);


        _logger.LogDebug("Set cache entry {Key} with expiration {Expiration} and tags {Tags}",
            key, expiration, string.Join(", ", tagSet));
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            RemoveFromTagIndex(key, entry.Tags);
            UpdateAccessOrder(key, remove: true);
            Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize());

            _logger.LogDebug("Removed cache entry {Key}", key);
        }
        return Task.CompletedTask;
    }

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (_tagToKeys.TryGetValue(tag, out var keys))
        {
            var keysToRemove = keys.Keys.ToList();

            foreach (var key in keysToRemove)
            {
                await RemoveAsync(key, cancellationToken);
            }

            _logger.LogDebug("Removed {Count} cache entries with tag {Tag}", keysToRemove.Count, tag);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                RemoveFromTagIndex(key, entry.Tags);
                UpdateAccessOrder(key, remove: true);
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
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

            return Task.FromResult(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for AdvancedMemoryStorageProvider");
            return Task.FromResult(HealthStatus.Unhealthy);
        }
    }

    public Task<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
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

        return Task.FromResult<StorageStats?>(stats);
    }

    private async Task CheckAndEvictIfNeeded(string newKey, CacheEntry newEntry)
    {
        var currentCount = _cache.Count;
        var estimatedSize = newEntry.EstimateSize();
        var currentMemory = Interlocked.Read(ref _estimatedMemoryUsage);

        if (currentCount >= _options.MaxEntries || currentMemory + estimatedSize > _options.MaxMemoryUsage)
        {
            await EvictEntries(1); // Evict at least one entry
        }

        Interlocked.Add(ref _estimatedMemoryUsage, estimatedSize);
    }

    private Task EvictEntries(int minToEvict)
    {
        var evicted = 0;
        var candidates = GetEvictionCandidates();

        foreach (var key in candidates)
        {
            if (_cache.TryRemove(key, out var entry))
            {
                RemoveFromTagIndex(key, entry.Tags);
                UpdateAccessOrder(key, remove: true);
                Interlocked.Add(ref _estimatedMemoryUsage, -entry.EstimateSize());
                Interlocked.Increment(ref _evictions);
                evicted++;

                if (evicted >= minToEvict)
                    break;
            }
        }

        _logger.LogDebug("Evicted {Count} entries using {Policy} policy", evicted, _options.EvictionPolicy);
        return Task.CompletedTask;
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
            return _accessOrder.ToList(); // Oldest first
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
        var random = new Random();
        return _cache.Keys.OrderBy(k => random.Next());
    }

    private void UpdateAccessOrder(string key, bool remove)
    {
        lock (_accessOrderLock)
        {
            if (remove)
            {
                if (_cache.TryGetValue(key, out var entry) && entry.OrderNode != null)
                {
                    _accessOrder.Remove(entry.OrderNode);
                    entry.OrderNode = null;
                }
            }
            else
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    // Remove from current position
                    if (entry.OrderNode != null)
                    {
                        _accessOrder.Remove(entry.OrderNode);
                    }

                    // Add to end (most recently used)
                    entry.OrderNode = _accessOrder.AddLast(key);
                }
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
                UpdateAccessOrder(key, remove: true);
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