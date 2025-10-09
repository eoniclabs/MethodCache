using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MethodCache.Core.Storage.Layers;

/// <summary>
/// Layer responsible for tracking key-to-tag and tag-to-key mappings.
/// Provides efficient O(K) tag invalidation where K is the number of keys with a given tag.
/// </summary>
public sealed class TagIndexLayer : IStorageLayer
{
    private readonly ILogger<TagIndexLayer> _logger;
    private readonly ConcurrentDictionary<string, string[]> _keyToTags = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new(StringComparer.Ordinal);

    private long _tagMappings;
    private long _tagInvalidations;

    public string LayerId => "TagIndex";
    public int Priority => 5; // Execute before storage layers to track tags
    public bool IsEnabled => true; // Always enabled for tag tracking

    public TagIndexLayer(ILogger<TagIndexLayer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initialized {LayerId} layer", LayerId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Tag index doesn't handle Get operations, just tracks tags
        // Populate context with known tags for this key (for promotion scenarios)
        if (_keyToTags.TryGetValue(key, out var tags) && tags.Length > 0)
        {
            context.Tags = tags;
        }

        // Don't stop propagation, let other layers handle the get
        return ValueTask.FromResult(StorageLayerResult<T>.NotHandled());
    }

    public ValueTask SetAsync<T>(
        StorageContext context,
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        var tagArray = tags as string[] ?? tags.ToArray();

        if (tagArray.Length == 0)
        {
            // No tags, remove from index
            RemoveKeyFromIndex(key);
            return ValueTask.CompletedTask;
        }

        // Update forward index: key → tags
        _keyToTags[key] = tagArray;

        // Update reverse index: tag → keys
        foreach (var tag in tagArray)
        {
            var keys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            keys.TryAdd(key, 0);
        }

        Interlocked.Increment(ref _tagMappings);
        _logger.LogTrace("{LayerId} tracked {TagCount} tags for key {Key}", LayerId, tagArray.Length, key);

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        RemoveKeyFromIndex(key);
        _logger.LogTrace("{LayerId} removed tag mappings for key {Key}", LayerId, key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        // Use reverse index for O(K) complexity
        // where K is the number of keys with this tag (vs O(N*M) scanning all keys)
        if (_tagToKeys.TryGetValue(tag, out var keys))
        {
            // Store keys to remove in context for other layers to use
            context.Metadata["RemovedKeys"] = keys.Keys.ToArray();

            foreach (var key in keys.Keys)
            {
                RemoveKeyFromIndex(key);
            }

            _tagToKeys.TryRemove(tag, out _);

            Interlocked.Increment(ref _tagInvalidations);
            _logger.LogDebug("{LayerId} invalidated {KeyCount} keys for tag {Tag}", LayerId, keys.Count, tag);
        }
        else
        {
            _logger.LogTrace("{LayerId} no keys found for tag {Tag}", LayerId, tag);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Tag index doesn't track existence, just tags
        // Return false to indicate this layer doesn't handle existence checks
        return ValueTask.FromResult(false);
    }

    public ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        var status = new LayerHealthStatus(
            LayerId,
            HealthStatus.Healthy,
            $"Tracking {_keyToTags.Count} key-tag mappings");

        return ValueTask.FromResult(status);
    }

    public LayerStats GetStats()
    {
        var additionalStats = new Dictionary<string, object>
        {
            ["KeyToTagMappings"] = _keyToTags.Count,
            ["TagToKeyMappings"] = _tagToKeys.Count,
            ["TotalTagMappings"] = Interlocked.Read(ref _tagMappings),
            ["TagInvalidations"] = Interlocked.Read(ref _tagInvalidations)
        };

        return new LayerStats(
            LayerId,
            0, // No hits/misses for tag index
            0,
            0.0,
            Interlocked.Read(ref _tagMappings),
            additionalStats);
    }

    public ValueTask DisposeAsync()
    {
        _keyToTags.Clear();
        _tagToKeys.Clear();
        _logger.LogInformation("Disposed {LayerId} layer", LayerId);
        return ValueTask.CompletedTask;
    }

    private void RemoveKeyFromIndex(string key)
    {
        // Remove from forward index
        if (_keyToTags.TryRemove(key, out var tags))
        {
            // Remove from reverse index
            foreach (var tag in tags)
            {
                if (_tagToKeys.TryGetValue(tag, out var keys))
                {
                    keys.TryRemove(key, out _);

                    // Clean up empty tag entries
                    if (keys.IsEmpty)
                    {
                        _tagToKeys.TryRemove(tag, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all keys associated with a given tag (for diagnostics).
    /// </summary>
    public string[] GetKeysByTag(string tag)
    {
        return _tagToKeys.TryGetValue(tag, out var keys)
            ? keys.Keys.ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Gets all tags associated with a given key (for diagnostics).
    /// </summary>
    public string[] GetTagsByKey(string key)
    {
        return _keyToTags.TryGetValue(key, out var tags)
            ? tags
            : Array.Empty<string>();
    }
}
