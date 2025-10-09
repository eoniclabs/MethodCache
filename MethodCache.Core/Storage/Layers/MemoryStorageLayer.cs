using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Layers;

/// <summary>
/// L1 memory storage layer providing fast in-memory caching.
/// This is typically the first layer in the pipeline and always enabled.
/// </summary>
public sealed class MemoryStorageLayer : IStorageLayer
{
    private readonly IMemoryStorage _memoryStorage;
    private readonly StorageLayerOptions _options;
    private readonly ILogger<MemoryStorageLayer> _logger;
    private readonly ICacheMetricsProvider? _metricsProvider;

    private long _hits;
    private long _misses;
    private long _sets;
    private long _removes;

    private const string MetricsKey = "HybridStorage:L1";

    public string LayerId => "L1";
    public int Priority => 10;
    public bool IsEnabled => true; // Memory layer is always enabled

    public MemoryStorageLayer(
        IMemoryStorage memoryStorage,
        IOptions<StorageLayerOptions> options,
        ILogger<MemoryStorageLayer> logger,
        ICacheMetricsProvider? metricsProvider = null)
    {
        _memoryStorage = memoryStorage ?? throw new ArgumentNullException(nameof(memoryStorage));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metricsProvider = metricsProvider;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initialized {LayerId} storage layer", LayerId);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await _memoryStorage.GetAsync<T>(key, cancellationToken);

        if (result != null)
        {
            Interlocked.Increment(ref _hits);
            context.LayersHit.Add(LayerId);
            _metricsProvider?.CacheHit(MetricsKey);
            _logger.LogDebug("{LayerId} cache hit for key {Key}", LayerId, key);

            // Memory layer always stops propagation on hit
            return StorageLayerResult<T>.Hit(result);
        }

        Interlocked.Increment(ref _misses);
        context.LayersMissed.Add(LayerId);
        _metricsProvider?.CacheMiss(MetricsKey);

        // Continue to next layer
        return StorageLayerResult<T>.Miss();
    }

    public async ValueTask SetAsync<T>(
        StorageContext context,
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        var l1Expiration = CalculateL1Expiration(expiration);
        await _memoryStorage.SetAsync(key, value, l1Expiration, tags, cancellationToken);

        Interlocked.Increment(ref _sets);
        _logger.LogDebug("{LayerId} set key {Key} with expiration {Expiration}", LayerId, key, l1Expiration);
    }

    public async ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        await _memoryStorage.RemoveAsync(key, cancellationToken);

        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed key {Key}", LayerId, key);
    }

    public async ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        await _memoryStorage.RemoveByTagAsync(tag, cancellationToken);

        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed all keys with tag {Tag}", LayerId, tag);
    }

    public ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        var exists = _memoryStorage.Exists(key);
        return ValueTask.FromResult(exists);
    }

    public ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        // Memory storage is generally always healthy
        var status = new LayerHealthStatus(
            LayerId,
            HealthStatus.Healthy,
            "Memory storage is operational");

        return ValueTask.FromResult(status);
    }

    public LayerStats GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        var hitRatio = total > 0 ? (double)hits / total : 0.0;

        var memStats = _memoryStorage.GetStats();

        var additionalStats = new Dictionary<string, object>
        {
            ["EntryCount"] = memStats.EntryCount,
            ["Evictions"] = memStats.Evictions,
            ["TagMappingCount"] = memStats.TagMappingCount,
            ["MemoryHitRatio"] = memStats.HitRatio,
            ["Sets"] = Interlocked.Read(ref _sets),
            ["Removes"] = Interlocked.Read(ref _removes)
        };

        return new LayerStats(
            LayerId,
            hits,
            misses,
            hitRatio,
            total,
            additionalStats);
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposed {LayerId} storage layer", LayerId);
        return ValueTask.CompletedTask;
    }

    private TimeSpan CalculateL1Expiration(TimeSpan originalExpiration)
    {
        // Respect the original expiration if it's shorter than max
        // Only enforce max as upper bound
        var l1Expiration = TimeSpan.FromTicks(Math.Min(
            originalExpiration.Ticks,
            _options.L1MaxExpiration.Ticks));

        return l1Expiration;
    }
}
