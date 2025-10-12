using MethodCache.Core.Infrastructure;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination.Layers;
using MethodCache.Core.Storage.Coordination.Supporting;
using MethodCache.Core.Storage.Layers.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Layers.Distributed;

/// <summary>
/// L2 distributed storage layer (e.g., Redis) providing shared cache across multiple instances.
/// Includes L1 promotion on hits and optional async write support.
/// </summary>
public sealed class DistributedStorageLayer : IStorageLayer
{
    private readonly IStorageProvider? _l2Storage;
    private readonly MemoryStorageLayer _l1Layer;
    private readonly AsyncWriteQueueLayer? _asyncQueue;
    private readonly StorageLayerOptions _options;
    private readonly ILogger<DistributedStorageLayer> _logger;
    private readonly ICacheMetricsProvider? _metricsProvider;
    private readonly SemaphoreSlim _semaphore;

    private long _hits;
    private long _misses;
    private long _sets;
    private long _removes;
    private long _promotions;

    private const string MetricsKey = "HybridStorage:L2";

    public string LayerId => "L2";
    public int Priority => 20;
    public bool IsEnabled { get; }

    public DistributedStorageLayer(
        IStorageProvider? l2Storage,
        MemoryStorageLayer l1Layer,
        IOptions<StorageLayerOptions> options,
        ILogger<DistributedStorageLayer> logger,
        ICacheMetricsProvider? metricsProvider = null,
        AsyncWriteQueueLayer? asyncQueue = null)
    {
        _l2Storage = l2Storage;
        _l1Layer = l1Layer ?? throw new ArgumentNullException(nameof(l1Layer));
        _asyncQueue = asyncQueue;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metricsProvider = metricsProvider;

        IsEnabled = l2Storage != null && _options.L2Enabled;
        _semaphore = new SemaphoreSlim(
            _options.MaxConcurrentL2Operations,
            _options.MaxConcurrentL2Operations);
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsEnabled)
        {
            _logger.LogInformation("Initialized {LayerId} storage layer (Provider: {Provider})",
                LayerId, _l2Storage?.Name ?? "None");
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l2Storage == null)
        {
            return StorageLayerResult<T>.NotHandled();
        }

        var waitSucceeded = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSucceeded = true;

            var result = await _l2Storage.GetAsync<T>(key, cancellationToken);

            if (result != null)
            {
                Interlocked.Increment(ref _hits);
                context.LayersHit.Add(LayerId);
                _metricsProvider?.CacheHit(MetricsKey);
                _logger.LogDebug("{LayerId} cache hit for key {Key}", LayerId, key);

                // Promote to L1 with shorter expiration
                await PromoteToL1Async(key, result, context.Tags ?? Array.Empty<string>(), cancellationToken);

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
            if (waitSucceeded)
            {
                _semaphore.Release();
            }
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
        if (!IsEnabled || _l2Storage == null)
        {
            return;
        }

        // Try async write first
        if (_options.EnableAsyncL2Writes && _asyncQueue != null)
        {
            var scheduled = _asyncQueue.TryScheduleWork(ct => WriteToL2Async(key, value, expiration, tags, ct));
            if (scheduled)
            {
                _logger.LogTrace("{LayerId} scheduled async write for key {Key}", LayerId, key);
                return;
            }

            _logger.LogDebug("{LayerId} async queue full, performing synchronous write for key {Key}", LayerId, key);
        }

        // Fallback to synchronous write
        await WriteToL2Async(key, value, expiration, tags, cancellationToken);
    }

    public async ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l2Storage == null)
        {
            return;
        }

        await ExecuteL2OperationAsync(() => _l2Storage.RemoveAsync(key, cancellationToken), cancellationToken);
        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed key {Key}", LayerId, key);
    }

    public async ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l2Storage == null)
        {
            return;
        }

        await ExecuteL2OperationAsync(() => _l2Storage.RemoveByTagAsync(tag, cancellationToken), cancellationToken);
        Interlocked.Increment(ref _removes);
        _logger.LogDebug("{LayerId} removed all keys with tag {Tag}", LayerId, tag);
    }

    public async ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l2Storage == null)
        {
            return false;
        }

        var waitSucceeded = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSucceeded = true;
            return await _l2Storage.ExistsAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking existence in {LayerId} storage for key {Key}", LayerId, key);
            return false;
        }
        finally
        {
            if (waitSucceeded)
            {
                _semaphore.Release();
            }
        }
    }

    public async ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || _l2Storage == null)
        {
            return new LayerHealthStatus(LayerId, HealthStatus.Healthy, "L2 storage is disabled");
        }

        try
        {
            var health = await _l2Storage.GetHealthAsync(cancellationToken);
            return new LayerHealthStatus(LayerId, health, $"L2 provider: {_l2Storage.Name}");
        }
        catch (Exception ex)
        {
            return new LayerHealthStatus(LayerId, HealthStatus.Unhealthy, $"L2 health check failed: {ex.Message}");
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
            ["ProviderName"] = _l2Storage?.Name ?? "None",
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

    private async ValueTask WriteToL2Async<T>(
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        if (_l2Storage == null)
        {
            return;
        }

        var waitSucceeded = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSucceeded = true;
            await _l2Storage.SetAsync(key, value, expiration, tags, cancellationToken);
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

    private async ValueTask ExecuteL2OperationAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
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

    private async ValueTask PromoteToL1Async<T>(string key, T value, string[] tags, CancellationToken cancellationToken)
    {
        try
        {
            var l1Expiration = TimeSpan.FromTicks(Math.Min(
                _options.L2DefaultExpiration.Ticks,
                _options.L1MaxExpiration.Ticks));

            var context = new StorageContext { Tags = tags };
            await _l1Layer.SetAsync(context, key, value, l1Expiration, tags, cancellationToken);

            Interlocked.Increment(ref _promotions);
            _logger.LogTrace("{LayerId} promoted key {Key} to L1", LayerId, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error promoting key {Key} from {LayerId} to L1", key, LayerId);
        }
    }
}
