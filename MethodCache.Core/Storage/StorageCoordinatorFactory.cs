using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration;
using MethodCache.Core.Storage.Layers;

namespace MethodCache.Core.Storage;

/// <summary>
/// Factory for creating StorageCoordinator instances with appropriate layer composition.
/// </summary>
public static class StorageCoordinatorFactory
{
    /// <summary>
    /// Creates a StorageCoordinator with the standard layer composition (L1 + optional L2 + optional L3 + backplane).
    /// This replaces the old HybridStorageManager constructor pattern.
    /// </summary>
    public static StorageCoordinator Create(
        IMemoryStorage memoryStorage,
        IOptions<StorageOptions> storageOptions,
        ILogger<StorageCoordinator> coordinatorLogger,
        IStorageProvider? l2Storage = null,
        IPersistentStorageProvider? l3Storage = null,
        IBackplane? backplane = null,
        ICacheMetricsProvider? metricsProvider = null)
    {
        if (memoryStorage == null) throw new ArgumentNullException(nameof(memoryStorage));
        if (storageOptions == null) throw new ArgumentNullException(nameof(storageOptions));
        if (coordinatorLogger == null) throw new ArgumentNullException(nameof(coordinatorLogger));

        // Convert StorageOptions to StorageLayerOptions
        var layerOptions = Microsoft.Extensions.Options.Options.Create(ConvertToLayerOptions(storageOptions.Value));

        // Create logger factory for layers (reuse coordinator logger's factory if possible)
        var loggerFactory = GetLoggerFactory(coordinatorLogger);

        var layers = new List<IStorageLayer>();

        // Priority 5: Tag Index Layer (always enabled)
        var tagIndexLayer = new TagIndexLayer(
            loggerFactory.CreateLogger<TagIndexLayer>());
        layers.Add(tagIndexLayer);

        // Priority 10: Memory Layer (L1 - always enabled)
        var memoryLayer = new MemoryStorageLayer(
            memoryStorage,
            layerOptions,
            loggerFactory.CreateLogger<MemoryStorageLayer>(),
            metricsProvider);
        layers.Add(memoryLayer);

        // Priority 15: Async Write Queue (if async writes enabled)
        AsyncWriteQueueLayer? asyncQueue = null;
        if (storageOptions.Value.EnableAsyncL2Writes || storageOptions.Value.EnableAsyncL3Writes)
        {
            asyncQueue = new AsyncWriteQueueLayer(
                layerOptions,
                loggerFactory.CreateLogger<AsyncWriteQueueLayer>());
            layers.Add(asyncQueue);
        }

        // Priority 20: Distributed Layer (L2 - if provided)
        DistributedStorageLayer? distributedLayer = null;
        if (l2Storage != null)
        {
            distributedLayer = new DistributedStorageLayer(
                l2Storage,
                memoryLayer,
                layerOptions,
                loggerFactory.CreateLogger<DistributedStorageLayer>(),
                metricsProvider,
                asyncQueue);
            layers.Add(distributedLayer);
        }

        // Priority 30: Persistent Layer (L3 - if provided)
        if (l3Storage != null)
        {
            var persistentLayer = new PersistentStorageLayer(
                l3Storage,
                memoryLayer,
                layerOptions,
                loggerFactory.CreateLogger<PersistentStorageLayer>(),
                metricsProvider,
                distributedLayer,
                asyncQueue);
            layers.Add(persistentLayer);
        }

        // Priority 100: Backplane Coordination (if provided)
        if (backplane != null)
        {
            var backplaneLayer = new BackplaneCoordinationLayer(
                backplane,
                memoryLayer,
                layerOptions,
                loggerFactory.CreateLogger<BackplaneCoordinationLayer>(),
                tagIndexLayer);
            layers.Add(backplaneLayer);
        }

        return new StorageCoordinator(layers, coordinatorLogger);
    }

    /// <summary>
    /// Converts StorageOptions to StorageLayerOptions.
    /// </summary>
    private static StorageLayerOptions ConvertToLayerOptions(StorageOptions storageOptions)
    {
        return new StorageLayerOptions
        {
            L1MaxExpiration = storageOptions.L1MaxExpiration,
            L2DefaultExpiration = storageOptions.L2DefaultExpiration,
            L3DefaultExpiration = storageOptions.L3DefaultExpiration,
            L3MaxExpiration = storageOptions.L3MaxExpiration,
            L2Enabled = storageOptions.L2Enabled,
            L3Enabled = storageOptions.L3Enabled,
            EnableAsyncL2Writes = storageOptions.EnableAsyncL2Writes,
            EnableAsyncL3Writes = storageOptions.EnableAsyncL3Writes,
            AsyncWriteQueueCapacity = storageOptions.AsyncWriteQueueCapacity,
            MaxConcurrentL2Operations = storageOptions.MaxConcurrentL2Operations,
            MaxConcurrentL3Operations = storageOptions.MaxConcurrentL3Operations,
            EnableL3Promotion = storageOptions.EnableL3Promotion,
            EnableBackplane = storageOptions.EnableBackplane,
            InstanceId = storageOptions.InstanceId
        };
    }

    /// <summary>
    /// Creates a simple logger factory wrapper from an existing logger.
    /// </summary>
    private static ILoggerFactory GetLoggerFactory(ILogger coordinatorLogger)
    {
        // Simple approach: wrap the existing logger
        // All layers will share the same logger instance
        return new LoggerFactoryWrapper(coordinatorLogger);
    }

    /// <summary>
    /// Simple wrapper that creates loggers from an existing logger's context.
    /// </summary>
    private sealed class LoggerFactoryWrapper : ILoggerFactory
    {
        private readonly ILogger _baseLogger;

        public LoggerFactoryWrapper(ILogger baseLogger)
        {
            _baseLogger = baseLogger;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _baseLogger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
            // No-op for wrapper
        }

        public void Dispose()
        {
            // No-op for wrapper
        }
    }
}
