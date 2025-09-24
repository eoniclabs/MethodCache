using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Infrastructure.Configuration;
using System.Collections.Concurrent;

namespace MethodCache.Infrastructure.Services
{
    /// <summary>
    /// Generic cache warming service that works with any storage provider
    /// </summary>
    public class CacheWarmingService : BackgroundService, ICacheWarmingService
    {
        private readonly IStorageProvider _storageProvider;
        private readonly StorageOptions _options;
        private readonly ILogger<CacheWarmingService> _logger;
        private readonly ConcurrentDictionary<string, CacheWarmupEntry> _warmupEntries = new();
        private readonly Timer? _warmupTimer;

        public CacheWarmingService(
            IStorageProvider storageProvider,
            IOptions<StorageOptions> options,
            ILogger<CacheWarmingService> logger)
        {
            _storageProvider = storageProvider;
            _options = options.Value;
            _logger = logger;

            if (_options.EnableCacheWarming)
            {
                // Check for warmup every 30 seconds
                _warmupTimer = new Timer(ProcessWarmupEntries, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
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
            _warmupTimer?.Dispose();
            _logger.LogInformation("Cache warming service stopped");
            return Task.CompletedTask;
        }

        public Task RegisterWarmupKeyAsync(string key, Func<Task<object>> factory, TimeSpan refreshInterval, string[]? tags = null)
        {
            if (!_options.EnableCacheWarming)
                return Task.CompletedTask;

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

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.EnableCacheWarming)
                return Task.CompletedTask;

            _logger.LogInformation("Cache warming background service started");

            // Run the warming loop in the background without blocking startup
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

                        if (!stoppingToken.IsCancellationRequested)
                        {
                            await ProcessWarmupEntriesAsync();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in cache warming background service");
                }
            }, stoppingToken);

            return Task.CompletedTask;
        }

        private void ProcessWarmupEntries(object? state)
        {
            _ = Task.Run(async () => await ProcessWarmupEntriesAsync());
        }

        private async Task ProcessWarmupEntriesAsync()
        {
            if (!_options.EnableCacheWarming || _warmupEntries.IsEmpty)
                return;

            var now = DateTimeOffset.UtcNow;
            var processedCount = 0;
            var errorCount = 0;

            foreach (var entry in _warmupEntries.Values)
            {
                try
                {
                    if (now >= entry.NextWarmupTime)
                    {
                        await WarmupCacheEntryAsync(entry, now);
                        processedCount++;
                    }
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

        private async Task WarmupCacheEntryAsync(CacheWarmupEntry entry, DateTimeOffset now)
        {
            try
            {
                // Check if entry exists and get its status
                var exists = await _storageProvider.ExistsAsync(entry.Key);

                // Calculate warmup threshold (warm when 25% of refresh interval remains)
                var warmupThreshold = entry.RefreshInterval.Multiply(0.25);
                var timeSinceLastWarm = now - entry.LastWarmedAt;

                // Warm up if key doesn't exist or it's time to refresh
                if (!exists || timeSinceLastWarm >= (entry.RefreshInterval - warmupThreshold))
                {
                    _logger.LogDebug("Warming up cache entry {Key} (exists: {Exists}, time since last warm: {TimeSinceWarm})",
                        entry.Key, exists, timeSinceLastWarm);

                    // Execute the factory to get fresh data
                    var freshData = await entry.Factory();

                    if (freshData != null)
                    {
                        // Cache the fresh data
                        await _storageProvider.SetAsync(entry.Key, freshData, entry.RefreshInterval, entry.Tags);

                        entry.LastWarmedAt = now;
                        _logger.LogDebug("Successfully warmed up cache entry {Key}", entry.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm up cache entry {Key}", entry.Key);
                throw;
            }
        }

        public override void Dispose()
        {
            _warmupTimer?.Dispose();
            base.Dispose();
        }
    }
}