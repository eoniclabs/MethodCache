using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Storage;

/// <summary>
/// Memory storage implementation using IMemoryCache.
/// </summary>
public class MemoryStorage : IMemoryStorage
{
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
    private readonly StorageOptions _options;
    private readonly ILogger<MemoryStorage> _logger;

    // Tag tracking infrastructure for efficient L1 invalidation
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _keyToTags;
    private readonly ReaderWriterLockSlim _tagMappingLock;
    private volatile int _tagMappingCount;

    // Statistics
    private long _hits;
    private long _misses;
    private long _evictions;

    public MemoryStorage(
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        IOptions<StorageOptions> options,
        ILogger<MemoryStorage> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        _tagToKeys = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        _keyToTags = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        _tagMappingLock = new ReaderWriterLockSlim();
    }

    public T? Get<T>(string key)
    {
        var result = _cache.Get<T>(key);

        if (result != null)
        {
            Interlocked.Increment(ref _hits);
        }
        else
        {
            Interlocked.Increment(ref _misses);
        }

        return result;
    }

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Get<T>(key));

    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        Set(key, value, expiration, Enumerable.Empty<string>());
    }

    public void Set<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration,
            Size = EstimateSize(value),
            PostEvictionCallbacks = { new PostEvictionCallbackRegistration
            {
                EvictionCallback = OnEvicted,
                State = key
            }}
        };

        _cache.Set(key, value, cacheOptions);

        // Track tags if enabled and tags are provided
        var tagArray = tags.ToArray();
        if (_options.EnableEfficientL1TagInvalidation && tagArray.Length > 0)
        {
            TrackTags(key, tagArray);
        }

        _logger.LogDebug("Set key {Key} in memory storage with expiration {Expiration}", key, expiration);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        Set(key, value, expiration);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        Set(key, value, expiration, tags);
        return ValueTask.CompletedTask;
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
        RemoveTagMappings(key);
        _logger.LogDebug("Removed key {Key} from memory storage", key);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Remove(key);
        return ValueTask.CompletedTask;
    }

    public void RemoveByTag(string tag)
    {
        if (!_options.EnableEfficientL1TagInvalidation)
        {
            // Fallback: clear entire L1 cache
            _logger.LogWarning("Clearing entire L1 cache due to tag invalidation for tag {Tag}", tag);
            Clear();
            return;
        }

        _tagMappingLock.EnterReadLock();
        try
        {
            if (_tagToKeys.TryGetValue(tag, out var keys))
            {
                var keysToRemove = keys.Keys.ToArray();
                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }

                _logger.LogDebug("Removed {Count} keys from memory storage for tag {Tag}", keysToRemove.Length, tag);
            }
        }
        finally
        {
            _tagMappingLock.ExitReadLock();
        }

        // Clean up tag mappings
        RemoveTagMappings(tag, isTagInvalidation: true);
    }

    public ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        RemoveByTag(tag);
        return ValueTask.CompletedTask;
    }

    public bool Exists(string key)
    {
        return _cache.TryGetValue(key, out object? _);
    }

    public MemoryStorageStats GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var evictions = Interlocked.Read(ref _evictions);

        // Use hits as proxy for entry count since we don't track precise entry count
        var approximateEntryCount = Math.Max(hits, 0);

        return new MemoryStorageStats
        {
            Hits = hits,
            Misses = misses,
            Evictions = evictions,
            TagMappingCount = _tagMappingCount,
            EntryCount = approximateEntryCount,
            EstimatedMemoryUsage = _tagMappingCount * 50 + approximateEntryCount * 200
        };
    }

    public void Clear()
    {
        if (_cache is MemoryCache mc)
        {
            mc.Clear();
        }

        // Clear tag mappings
        _tagMappingLock.EnterWriteLock();
        try
        {
            _tagToKeys.Clear();
            _keyToTags.Clear();
            _tagMappingCount = 0;
        }
        finally
        {
            _tagMappingLock.ExitWriteLock();
        }

        _logger.LogDebug("Cleared all entries from memory storage");
    }

    private void TrackTags(string key, string[] tags)
    {
        if (_tagMappingCount >= _options.MaxTagMappings)
        {
            _logger.LogWarning("Tag mapping limit reached, not tracking tags for key {Key}", key);
            return;
        }

        _tagMappingLock.EnterWriteLock();
        try
        {
            // Track key -> tags mapping
            var keyTags = _keyToTags.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>());
            foreach (var tag in tags)
            {
                keyTags.TryAdd(tag, 0);

                // Track tag -> keys mapping
                var tagKeys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>());
                tagKeys.TryAdd(key, 0);

                _tagMappingCount++;
            }
        }
        finally
        {
            _tagMappingLock.ExitWriteLock();
        }
    }

    private void RemoveTagMappings(string key)
    {
        if (!_options.EnableEfficientL1TagInvalidation)
            return;

        _tagMappingLock.EnterWriteLock();
        try
        {
            if (_keyToTags.TryRemove(key, out var tags))
            {
                foreach (var tag in tags.Keys)
                {
                    if (_tagToKeys.TryGetValue(tag, out var keys))
                    {
                        keys.TryRemove(key, out _);
                        if (keys.IsEmpty)
                        {
                            _tagToKeys.TryRemove(tag, out _);
                        }
                    }
                    _tagMappingCount--;
                }
            }
        }
        finally
        {
            _tagMappingLock.ExitWriteLock();
        }
    }

    private void RemoveTagMappings(string tag, bool isTagInvalidation)
    {
        if (!_options.EnableEfficientL1TagInvalidation)
            return;

        _tagMappingLock.EnterWriteLock();
        try
        {
            if (_tagToKeys.TryRemove(tag, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    if (_keyToTags.TryGetValue(key, out var keyTags))
                    {
                        keyTags.TryRemove(tag, out _);
                        if (keyTags.IsEmpty)
                        {
                            _keyToTags.TryRemove(key, out _);
                        }
                    }
                    _tagMappingCount--;
                }
            }
        }
        finally
        {
            _tagMappingLock.ExitWriteLock();
        }
    }

    private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        Interlocked.Increment(ref _evictions);

        if (state is string keyString)
        {
            RemoveTagMappings(keyString);
        }

        _logger.LogDebug("Key {Key} evicted from memory storage, reason: {Reason}", key, reason);
    }

    private static long EstimateSize<T>(T value)
    {
        // Simple size estimation - can be made more sophisticated
        return value switch
        {
            string s => s.Length * 2, // Unicode characters
            byte[] b => b.Length,
            int => 4,
            long => 8,
            double => 8,
            _ => 100 // Default estimate for complex objects
        };
    }

    private long GetEstimatedMemoryUsage()
    {
        // This is a rough estimation - in real implementation you might want to use more sophisticated memory measurement
        // Use hit count as a proxy for entry count to avoid circular dependency
        var approximateEntryCount = Math.Max(Interlocked.Read(ref _hits), 10);
        return _tagMappingCount * 50 + approximateEntryCount * 200; // Rough estimate
    }
}
