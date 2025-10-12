using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Infrastructure.Services;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Providers.Redis.Features
{
    /// <summary>
    /// Redis-specific cache warming service with TTL optimization
    /// </summary>
    public class RedisCacheWarmingService : BackgroundService, ICacheWarmingService
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly IRedisSerializer _serializer;
        private readonly IRedisTagManager _tagManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisCacheWarmingService> _logger;
        private readonly ConcurrentDictionary<string, CacheWarmupEntry> _warmupEntries = new();
        private readonly Timer? _warmupTimer;

        public RedisCacheWarmingService(
            IRedisConnectionManager connectionManager,
            IRedisSerializer serializer,
            IRedisTagManager tagManager,
            IOptions<RedisOptions> options,
            ILogger<RedisCacheWarmingService> logger)
        {
            _connectionManager = connectionManager;
            _serializer = serializer;
            _tagManager = tagManager;
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

            var fullKey = _options.KeyPrefix + key;
            var entry = new CacheWarmupEntry(fullKey, factory, refreshInterval, tags ?? Array.Empty<string>());

            _warmupEntries.AddOrUpdate(fullKey, entry, (_, _) => entry);

            _logger.LogDebug("Registered cache warmup for key {Key} with interval {Interval}", fullKey, refreshInterval);
            return Task.CompletedTask;
        }

        public Task UnregisterWarmupKeyAsync(string key)
        {
            var fullKey = _options.KeyPrefix + key;
            _warmupEntries.TryRemove(fullKey, out _);

            _logger.LogDebug("Unregistered cache warmup for key {Key}", fullKey);
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
                var database = _connectionManager.GetDatabase();

                // Check if entry still exists and is close to expiration (Redis-specific TTL check)
                var ttl = await database.KeyTimeToLiveAsync(entry.Key);

                // If key doesn't exist or expires within the next 25% of refresh interval, warm it up
                var warmupThreshold = entry.RefreshInterval.Multiply(0.25);

                if (!ttl.HasValue || ttl.Value <= warmupThreshold)
                {
                    _logger.LogDebug("Warming up cache entry {Key} (TTL: {TTL})", entry.Key, ttl?.ToString() ?? "expired");

                    // Execute the factory to get fresh data
                    var freshData = await entry.Factory();

                    if (freshData != null)
                    {
                        // Serialize and cache the fresh data
                        var serializedData = await _serializer.SerializeAsync(freshData);
                        await database.StringSetAsync(entry.Key, serializedData, entry.RefreshInterval);

                        // Update tag associations if needed
                        if (entry.Tags.Any())
                        {
                            await _tagManager.AssociateTagsAsync(entry.Key, entry.Tags);
                        }

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