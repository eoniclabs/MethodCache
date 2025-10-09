using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Layers;

/// <summary>
/// L3 persistent storage layer (e.g., SQL Server) providing long-term cache persistence.
/// Includes L1/L2 promotion on hits and optional async write support.
/// </summary>
public sealed class PersistentStorageLayer : IStorageLayer
{
    private readonly IPersistentStorageProvider? _l3Storage;
    private readonly MemoryStorageLayer _l1Layer;
    private readonly DistributedStorageLayer? _l2Layer;
    private readonly AsyncWriteQueueLayer? _asyncQueue;
    private readonly StorageLayerOptions _options;
    private readonly ILogger<PersistentStorageLayer> _logger;
    private readonly ICacheMetricsProvider? _metricsProvider;
    private readonly SemaphoreSlim _semaphore;

    private long _hits;
    private long _misses;
    private long _sets;
    private long _removes;
    private long _promotions;

    private const string MetricsKey = "HybridStorage:L3";

    public string LayerId => "L3";
    public int Priority => 30;
    public bool IsEnabled { get; }

    public PersistentStorageLayer(
        IPersistentStorageProvider? l3Storage,
        MemoryStorageLayer l1Layer,
        IOptions<StorageLayerOptions> options,
        ILogger<PersistentStorageLayer> logger,
        ICacheMetricsProvider? metricsProvider = null,
        DistributedStorageLayer? l2Layer = null,
        AsyncWriteQueueLayer? asyncQueue = null)
    {
        _l3Storage = l3Storage;
        _l1Layer = l1Layer ?? throw new ArgumentNullException(nameof(l1Layer));
        _l2Layer = l2Layer;
        _asyncQueue = asyncQueue;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metricsProvider = metricsProvider;

        IsEnabled = l3Storage != null && _options.L3Enabled;
        _semaphore = new SemaphoreSlim(
            _options.MaxConcurrentL3Operations,
            _options.MaxConcurrentL3Operations);
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsEnabled)
        {
            _logger.LogInformation("Initialized {LayerId} storage layer (Provider: {Provider})",
                LayerId, _l3Storage?.Name ?? "None");
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return StorageLayerResult<T>.NotHandled();
        }

        try
        {
            await _semaphore.WaitAsync(cancellationToken);

            var result = await _l3Storage.GetAsync<T>(key, cancellationToken);

            if (result != null)
            {
                Interlocked.Increment(ref _hits);
                context.LayersHit.Add(LayerId);
                _metricsProvider?.CacheHit(MetricsKey);
                _logger.LogDebug("{LayerId} cache hit for key {Key}", LayerId, key);

                // Promote to higher layers if enabled
                if (_options.EnableL3Promotion)
                {
                    await PromoteToHigherLayersAsync(key, result, context.Tags ?? Array.Empty<string>(), cancellationToken);
                }

                return StorageLayerResult<T>.Hit(result);
            }

            Interlocked.Increment(ref _misses);
            context.LayersMissed.Add(LayerId);
            _metricsProvider?.CacheMiss(MetricsKey);

            return StorageLayerResult<T>.Miss();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accessing {LayerId} storage for key {Key}", LayerId, key);
            Interlocked.Increment(ref _misses);
            return StorageLayerResult<T>.Miss();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask SetAsync<T>(
        StorageContext context,
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return;
        }

        var l3Expiration = CalculateL3Expiration(expiration);

        // Try async write first
        if (_options.EnableAsyncL3Writes && _asyncQueue != null)
        {
            var scheduled = _asyncQueue.TryScheduleWork(ct => WriteToL3Async(key, value, l3Expiration, tags, ct));
            if (scheduled)
            {
                _logger.LogTrace("{LayerId} scheduled async write for key {Key}", LayerId, key);
                return;
            }

            _logger.LogDebug("{LayerId} async queue full, performing synchronous write for key {Key}", LayerId, key);
        }

        // Fallback to synchronous write
        await WriteToL3Async(key, value, l3Expiration, tags, cancellationToken);
    }

    public async ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return;
        }

        await ExecuteL3OperationAsync(() => _l3Storage.RemoveAsync(key, cancellationToken), cancellationToken);
        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed key {Key}", LayerId, key);
    }

    public async ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return;
        }

        await ExecuteL3OperationAsync(() => _l3Storage.RemoveByTagAsync(tag, cancellationToken), cancellationToken);
        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed all keys with tag {Tag}", LayerId, tag);
    }

    public async ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return false;
        }

        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            return await _l3Storage.ExistsAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking existence in {LayerId} storage for key {Key}", LayerId, key);
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l3Storage == null)
        {
            return new LayerHealthStatus(LayerId, HealthStatus.Healthy, "L3 storage is disabled");
        }

        try
        {
            var health = await _l3Storage.GetHealthAsync(cancellationToken);
            return new LayerHealthStatus(LayerId, health, $"L3 provider: {_l3Storage.Name}");
        }
        catch (Exception ex)
        {
            return new LayerHealthStatus(LayerId, HealthStatus.Unhealthy, $"L3 health check failed: {ex.Message}");
        }
    }

    public LayerStats GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        var hitRatio = total > 0 ? (double)hits / total : 0.0;

        var additionalStats = new Dictionary<string, object>
        {
            ["Sets"] = Interlocked.Read(ref _sets),
            ["Removes"] = Interlocked.Read(ref _removes),
            ["Promotions"] = Interlocked.Read(ref _promotions),
            ["ProviderName"] = _l3Storage?.Name ?? "None",
            ["Enabled"] = IsEnabled
        };

        return new LayerStats(LayerId, hits, misses, hitRatio, total, additionalStats);
    }

    public async ValueTask DisposeAsync()
    {
        _semaphore.Dispose();
        _logger.LogInformation("Disposed {LayerId} storage layer", LayerId);
        await ValueTask.CompletedTask;
    }

    private TimeSpan CalculateL3Expiration(TimeSpan originalExpiration)
    {
        // L3 typically has longer expiration times
        var l3Expiration = TimeSpan.FromTicks(Math.Max(
            originalExpiration.Ticks,
            _options.L3DefaultExpiration.Ticks));

        return TimeSpan.FromTicks(Math.Min(
            l3Expiration.Ticks,
            _options.L3MaxExpiration.Ticks));
    }

    private async ValueTask WriteToL3Async<T>(
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        if (_l3Storage == null)
        {
            return;
        }

        var waitSucceeded = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSucceeded = true;
            await _l3Storage.SetAsync(key, value, expiration, tags, cancellationToken);
            Interlocked.Increment(ref _sets);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{LayerId} write for key {Key} was cancelled due to shutdown", LayerId, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to {LayerId} storage for key {Key}", LayerId, key);
        }
        finally
        {
            if (waitSucceeded)
            {
                _semaphore.Release();
            }
        }
    }

    private async ValueTask ExecuteL3OperationAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        var waitSucceeded = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSucceeded = true;
            await operation();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{LayerId} operation was cancelled due to shutdown", LayerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in {LayerId} storage operation", LayerId);
        }
        finally
        {
            if (waitSucceeded)
            {
                _semaphore.Release();
            }
        }
    }

    private async ValueTask PromoteToHigherLayersAsync<T>(
        string key,
        T value,
        string[] tags,
        CancellationToken cancellationToken)
    {
        try
        {
            var promotionTasks = new List<ValueTask>();
            var context = new StorageContext { Tags = tags };

            // Promote to L1
            var l1Expiration = TimeSpan.FromTicks(Math.Min(
                _options.L3DefaultExpiration.Ticks,
                _options.L1MaxExpiration.Ticks));
            promotionTasks.Add(_l1Layer.SetAsync(context, key, value, l1Expiration, tags, cancellationToken));

            // Also promote to L2 if available
            if (_l2Layer != null && _l2Layer.IsEnabled)
            {
                var l2Expiration = _options.L2DefaultExpiration;
                promotionTasks.Add(_l2Layer.SetAsync(context, key, value, l2Expiration, tags, cancellationToken));
            }

            // Execute all promotions
            foreach (var task in promotionTasks)
            {
                await task;
            }

            Interlocked.Increment(ref _promotions);
            _logger.LogTrace("{LayerId} promoted key {Key} to higher layers", LayerId, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error promoting key {Key} from {LayerId} to higher layers", key, LayerId);
        }
    }
}
