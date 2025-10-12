using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;
using System.Collections.Concurrent;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Core.Infrastructure.Services
{
    /// <summary>
    /// Generic cache warming service that works with any storage provider.
    /// </summary>
    public class CacheWarmingService : BackgroundService, ICacheWarmingService
    {
        private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromSeconds(30);

        private readonly IStorageProvider _storageProvider;
        private readonly StorageOptions _options;
        private readonly ILogger<CacheWarmingService> _logger;
        private readonly ConcurrentDictionary<string, CacheWarmupEntry> _warmupEntries = new();

        public CacheWarmingService(
            IStorageProvider storageProvider,
            IOptions<StorageOptions> options,
            ILogger<CacheWarmingService> logger)
        {
            _storageProvider = storageProvider;
            _options = options.Value;
            _logger = logger;
        }

        public Task StartAsync()
        {
            if (!_options.EnableCacheWarming)
            {
                _logger.LogInformation("Cache warming is disabled");
                return Task.CompletedTask;
            }

            _logger.LogInformation("Cache warming service started with {EntryCount} warmup entries", _warmupEntries.Count);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _logger.LogInformation("Cache warming service stopped");
            return Task.CompletedTask;
        }

        public Task RegisterWarmupKeyAsync(string key, Func<Task<object>> factory, TimeSpan refreshInterval, string[]? tags = null)
        {
            if (!_options.EnableCacheWarming)
            {
                return Task.CompletedTask;
            }

            var entry = new CacheWarmupEntry(key, factory, refreshInterval, tags ?? Array.Empty<string>());
            _warmupEntries.AddOrUpdate(key, entry, (_, _) => entry);

            _logger.LogDebug("Registered cache warmup for key {Key} with interval {Interval}", key, refreshInterval);
            return Task.CompletedTask;
        }

        public Task UnregisterWarmupKeyAsync(string key)
        {
            _warmupEntries.TryRemove(key, out _);
            _logger.LogDebug("Unregistered cache warmup for key {Key}", key);
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.EnableCacheWarming)
            {
                return;
            }

            _logger.LogInformation("Cache warming background service started");

            using var timer = new PeriodicTimer(WarmupPollInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await ProcessWarmupEntriesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cache warming background service");
            }
            finally
            {
                _logger.LogInformation("Cache warming background service stopped");
            }
        }

        private async Task ProcessWarmupEntriesAsync(CancellationToken cancellationToken)
        {
            if (_warmupEntries.IsEmpty)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var processedCount = 0;
            var errorCount = 0;

            foreach (var entry in _warmupEntries.Values)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (now >= entry.NextWarmupTime)
                    {
                        await WarmupCacheEntryAsync(entry, now, cancellationToken).ConfigureAwait(false);
                        processedCount++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _logger.LogError(ex, "Error warming up cache entry {Key}", entry.Key);
                }
            }

            if (processedCount > 0 || errorCount > 0)
            {
                _logger.LogDebug("Cache warmup cycle completed: {ProcessedCount} entries warmed, {ErrorCount} errors",
                    processedCount, errorCount);
            }
        }

        private async Task WarmupCacheEntryAsync(CacheWarmupEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
        {
            try
            {
                var exists = await _storageProvider.ExistsAsync(entry.Key, cancellationToken).ConfigureAwait(false);

                // Calculate warmup threshold (warm when 25% of refresh interval remains)
                var warmupThreshold = entry.RefreshInterval.Multiply(0.25);
                var timeSinceLastWarm = now - entry.LastWarmedAt;

                if (!exists || timeSinceLastWarm >= (entry.RefreshInterval - warmupThreshold))
                {
                    _logger.LogDebug("Warming up cache entry {Key} (exists: {Exists}, time since last warm: {TimeSinceWarm})",
                        entry.Key, exists, timeSinceLastWarm);

                    var freshData = await entry.Factory().ConfigureAwait(false);

                    if (freshData != null)
                    {
                        await _storageProvider.SetAsync(entry.Key, freshData, entry.RefreshInterval, entry.Tags, cancellationToken).ConfigureAwait(false);

                        entry.LastWarmedAt = now;
                        _logger.LogDebug("Successfully warmed up cache entry {Key}", entry.Key);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm up cache entry {Key}", entry.Key);
                throw;
            }
        }
    }
}
