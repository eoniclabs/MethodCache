using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MethodCache.Core.Storage.Layers;

namespace MethodCache.Core.Storage;

/// <summary>
/// Thin coordinator that composes multiple storage layers into a unified storage strategy.
/// Executes layers in priority order, with each layer handling its specific responsibility.
/// </summary>
public sealed class StorageCoordinator : IStorageProvider, IAsyncDisposable
{
    private readonly IReadOnlyList<IStorageLayer> _layers;
    private readonly ILogger<StorageCoordinator> _logger;
    private int _disposed;

    public string Name { get; }

    public StorageCoordinator(
        IEnumerable<IStorageLayer> layers,
        ILogger<StorageCoordinator> logger,
        string? name = null)
    {
        _layers = (layers ?? throw new ArgumentNullException(nameof(layers)))
            .OrderBy(l => l.Priority)
            .ToList();

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Build name from enabled layers
        var enabledLayers = _layers.Where(l => l.IsEnabled).Select(l => l.LayerId).ToArray();
        Name = name ?? $"Coordinator({string.Join("+", enabledLayers)})";
    }

    /// <summary>
    /// Initializes all layers in the coordinator.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var layer in _layers)
        {
            await layer.InitializeAsync(cancellationToken);
        }

        _logger.LogInformation("Initialized {CoordinatorName} with {LayerCount} layers: {Layers}",
            Name,
            _layers.Count(l => l.IsEnabled),
            string.Join(", ", _layers.Where(l => l.IsEnabled).Select(l => l.LayerId)));
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext();

        foreach (var layer in _layers.Where(l => l.IsEnabled))
        {
            var result = await layer.GetAsync<T>(context, key, cancellationToken);

            if (result.Found)
            {
                _logger.LogTrace("Get operation for key {Key} completed by layer {LayerId}", key, layer.LayerId);
                return result.Value;
            }

            if (result.StopPropagation)
            {
                _logger.LogTrace("Get operation for key {Key} stopped at layer {LayerId}", key, layer.LayerId);
                break;
            }
        }

        _logger.LogDebug("Get operation for key {Key} completed. Layers hit: {LayersHit}, Layers missed: {LayersMissed}",
            key, string.Join(", ", context.LayersHit), string.Join(", ", context.LayersMissed));

        return default;
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        return SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async ValueTask SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var context = new StorageContext { Tags = tags.ToArray() };

        // Execute all enabled layers in parallel for Set operations
        // Each layer decides whether to execute synchronously or asynchronously
        var tasks = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.SetAsync(context, key, value, expiration, tags, cancellationToken).AsTask());

        await Task.WhenAll(tasks);

        _logger.LogTrace("Set operation for key {Key} completed across {LayerCount} layers",
            key, _layers.Count(l => l.IsEnabled));
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext();

        // Execute all enabled layers in parallel for Remove operations
        var tasks = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.RemoveAsync(context, key, cancellationToken).AsTask());

        await Task.WhenAll(tasks);

        _logger.LogTrace("Remove operation for key {Key} completed across {LayerCount} layers",
            key, _layers.Count(l => l.IsEnabled));
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext();

        // Execute all enabled layers in parallel for RemoveByTag operations
        var tasks = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.RemoveByTagAsync(context, tag, cancellationToken).AsTask());

        await Task.WhenAll(tasks);

        _logger.LogDebug("RemoveByTag operation for tag {Tag} completed across {LayerCount} layers",
            tag, _layers.Count(l => l.IsEnabled));
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext();

        // Check layers in priority order, return true on first hit
        foreach (var layer in _layers.Where(l => l.IsEnabled))
        {
            if (await layer.ExistsAsync(context, key, cancellationToken))
            {
                _logger.LogTrace("Key {Key} exists in layer {LayerId}", key, layer.LayerId);
                return true;
            }
        }

        return false;
    }

    public async ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var layerHealthTasks = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.GetHealthAsync(cancellationToken).AsTask());

        var layerHealthStatuses = await Task.WhenAll(layerHealthTasks);

        // Calculate overall health
        var healthyCount = layerHealthStatuses.Count(h => h.Status == HealthStatus.Healthy);
        var degradedCount = layerHealthStatuses.Count(h => h.Status == HealthStatus.Degraded);
        var unhealthyCount = layerHealthStatuses.Count(h => h.Status == HealthStatus.Unhealthy);
        var totalEnabled = _layers.Count(l => l.IsEnabled);

        if (unhealthyCount > 0)
        {
            _logger.LogWarning("Storage health check: {UnhealthyCount}/{TotalCount} layers unhealthy",
                unhealthyCount, totalEnabled);
            return HealthStatus.Unhealthy;
        }

        if (degradedCount > 0)
        {
            _logger.LogInformation("Storage health check: {DegradedCount}/{TotalCount} layers degraded",
                degradedCount, totalEnabled);
            return HealthStatus.Degraded;
        }

        _logger.LogDebug("Storage health check: All {TotalCount} layers healthy", totalEnabled);
        return HealthStatus.Healthy;
    }

    public ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var layerStats = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.GetStats())
            .ToList();

        // Aggregate stats from all layers
        var totalHits = layerStats.Sum(s => s.Hits);
        var totalMisses = layerStats.Sum(s => s.Misses);
        var totalOperations = layerStats.Sum(s => s.Operations);
        var totalHitRatio = totalHits + totalMisses > 0
            ? (double)totalHits / (totalHits + totalMisses)
            : 0.0;

        var additionalStats = new Dictionary<string, object>
        {
            ["CoordinatorName"] = Name,
            ["EnabledLayerCount"] = _layers.Count(l => l.IsEnabled),
            ["TotalLayerCount"] = _layers.Count,
            ["TotalHits"] = totalHits,
            ["TotalMisses"] = totalMisses,
            ["TotalHitRatio"] = totalHitRatio
        };

        // Include per-layer stats
        foreach (var stat in layerStats)
        {
            additionalStats[$"{stat.LayerId}Stats"] = stat;
            // Also expose individual values for easy access
            additionalStats[$"{stat.LayerId}Hits"] = stat.Hits;
            additionalStats[$"{stat.LayerId}Misses"] = stat.Misses;
            additionalStats[$"{stat.LayerId}Operations"] = stat.Operations;
            additionalStats[$"{stat.LayerId}HitRatio"] = stat.HitRatio;

            // Also expose layer-specific additional stats (like EntryCount, Evictions, etc.)
            if (stat.AdditionalStats != null)
            {
                foreach (var kvp in stat.AdditionalStats)
                {
                    additionalStats[$"{stat.LayerId}{kvp.Key}"] = kvp.Value;
                }
            }
        }

        var stats = new StorageStats
        {
            GetOperations = totalHits + totalMisses,
            SetOperations = totalOperations,
            RemoveOperations = 0, // Would need tracking
            AverageResponseTimeMs = 0, // Would need tracking
            ErrorCount = 0, // Would need tracking
            AdditionalStats = additionalStats
        };

        return ValueTask.FromResult<StorageStats?>(stats);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _logger.LogInformation("Disposing {CoordinatorName} with {LayerCount} layers", Name, _layers.Count);

        // Dispose layers in reverse priority order (highest priority first)
        foreach (var layer in _layers.Reverse())
        {
            try
            {
                await layer.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing layer {LayerId}", layer.LayerId);
            }
        }

        _logger.LogInformation("Disposed {CoordinatorName}", Name);
    }

    /// <summary>
    /// Gets diagnostic information about the coordinator and its layers.
    /// </summary>
    public CoordinatorDiagnostics GetDiagnostics()
    {
        return new CoordinatorDiagnostics
        {
            Name = Name,
            TotalLayers = _layers.Count,
            EnabledLayers = _layers.Count(l => l.IsEnabled),
            Layers = _layers.Select(l => new LayerDiagnosticInfo
            {
                LayerId = l.LayerId,
                Priority = l.Priority,
                IsEnabled = l.IsEnabled,
                Stats = l.GetStats()
            }).ToList()
        };
    }
}

/// <summary>
/// Diagnostic information about the storage coordinator.
/// </summary>
public sealed class CoordinatorDiagnostics
{
    public string Name { get; init; } = string.Empty;
    public int TotalLayers { get; init; }
    public int EnabledLayers { get; init; }
    public List<LayerDiagnosticInfo> Layers { get; init; } = new();
}

/// <summary>
/// Diagnostic information about a single layer.
/// </summary>
public sealed class LayerDiagnosticInfo
{
    public string LayerId { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool IsEnabled { get; init; }
    public LayerStats Stats { get; init; } = LayerStats.Empty("unknown");
}
