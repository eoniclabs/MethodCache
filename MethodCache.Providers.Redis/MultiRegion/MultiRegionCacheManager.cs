using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public class MultiRegionCacheManager : IMultiRegionCacheManager, IDisposable
    {
        private readonly MultiRegionOptions _options;
        private readonly IRegionSelector _regionSelector;
        private readonly ILogger<MultiRegionCacheManager> _logger;
        private readonly ConcurrentDictionary<string, IConnectionMultiplexer> _connections;
        private readonly Timer _healthCheckTimer;
        private readonly Timer? _syncTimer;
        private readonly SemaphoreSlim _syncSemaphore;
        private bool _disposed = false;

        public MultiRegionCacheManager(
            IOptions<MultiRegionOptions> options,
            IRegionSelector regionSelector,
            ILogger<MultiRegionCacheManager> logger)
        {
            _options = options.Value;
            _regionSelector = regionSelector;
            _logger = logger;
            _connections = new ConcurrentDictionary<string, IConnectionMultiplexer>();
            _syncSemaphore = new SemaphoreSlim(_options.MaxConcurrentSyncs, _options.MaxConcurrentSyncs);

            InitializeConnections();

            // Start health check timer
            _healthCheckTimer = new Timer(PerformHealthChecks, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            // Start cross-region sync timer
            if (_options.CrossRegionSyncInterval > TimeSpan.Zero)
            {
                _syncTimer = new Timer(PerformCrossRegionSync, null, 
                    _options.CrossRegionSyncInterval, _options.CrossRegionSyncInterval);
            }
        }

        public async Task<T?> GetFromRegionAsync<T>(string key, string region)
        {
            try
            {
                var connection = GetConnection(region);
                if (connection == null)
                    return default;

                var database = connection.GetDatabase();
                var regionKey = $"region:{region}:{key}";
                var value = await database.StringGetAsync(regionKey);
                
                if (!value.HasValue)
                    return default;

                // Deserialize the value (simplified - would use proper serializer)
                return System.Text.Json.JsonSerializer.Deserialize<T>(value!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get key {Key} from region {Region}", key, region);
                return default;
            }
        }

        public async Task SetInRegionAsync<T>(string key, T value, TimeSpan expiration, string region)
        {
            try
            {
                var connection = GetConnection(region);
                if (connection == null)
                    return;

                var database = connection.GetDatabase();
                var serializedValue = System.Text.Json.JsonSerializer.Serialize(value);
                var regionKey = $"region:{region}:{key}";
                
                await database.StringSetAsync(regionKey, serializedValue, expiration);
                
                _logger.LogDebug("Set key {Key} in region {Region} with expiration {Expiration}", key, region, expiration);

                // Handle replication based on strategy
                if (_options.Regions.First(r => r.Name == region).ReplicationStrategy != RegionReplicationStrategy.None)
                {
                    _ = Task.Run(() => ReplicateToOtherRegionsAsync(key, value, expiration, region));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set key {Key} in region {Region}", key, region);
                throw;
            }
        }

        public async Task InvalidateInRegionAsync(string key, string region)
        {
            try
            {
                var connection = GetConnection(region);
                if (connection == null)
                    return;

                var database = connection.GetDatabase();
                var regionKey = $"region:{region}:{key}";
                await database.KeyDeleteAsync(regionKey);
                
                _logger.LogDebug("Invalidated key {Key} in region {Region}", key, region);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to invalidate key {Key} in region {Region}", key, region);
            }
        }

        public async Task InvalidateGloballyAsync(string key)
        {
            var tasks = _options.Regions.Select(region => InvalidateInRegionAsync(key, region.Name));
            await Task.WhenAll(tasks);
            
            _logger.LogDebug("Invalidated key {Key} globally across all regions", key);
        }

        public async Task<Dictionary<string, T?>> GetFromMultipleRegionsAsync<T>(string key, IEnumerable<string> regions)
        {
            var tasks = regions.Select(async region =>
            {
                var value = await GetFromRegionAsync<T>(key, region);
                return new KeyValuePair<string, T?>(region, value);
            });

            var results = await Task.WhenAll(tasks);
            return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public async Task SyncToRegionAsync(string key, string sourceRegion, string targetRegion)
        {
            try
            {
                await _syncSemaphore.WaitAsync();

                var sourceConnection = GetConnection(sourceRegion);
                var targetConnection = GetConnection(targetRegion);

                if (sourceConnection == null || targetConnection == null)
                    return;

                var sourceDb = sourceConnection.GetDatabase();
                var targetDb = targetConnection.GetDatabase();

                var sourceKey = $"region:{sourceRegion}:{key}";
                var targetKey = $"region:{targetRegion}:{key}";

                // Get value from source
                var value = await sourceDb.StringGetAsync(sourceKey);
                if (!value.HasValue)
                    return;

                // Get TTL from source
                var ttl = await sourceDb.KeyTimeToLiveAsync(sourceKey);
                
                // Set in target with same TTL
                if (ttl.HasValue)
                    await targetDb.StringSetAsync(targetKey, value, ttl);
                else
                    await targetDb.StringSetAsync(targetKey, value);

                _logger.LogDebug("Synced key {Key} from {SourceRegion} to {TargetRegion}", key, sourceRegion, targetRegion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync key {Key} from {SourceRegion} to {TargetRegion}", 
                    key, sourceRegion, targetRegion);
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        public async Task<RegionHealthStatus> GetRegionHealthAsync(string region)
        {
            var stopwatch = Stopwatch.StartNew();
            var health = new RegionHealthStatus
            {
                Region = region,
                LastChecked = DateTime.UtcNow
            };

            try
            {
                var connection = GetConnection(region);
                if (connection == null)
                {
                    health.IsHealthy = false;
                    health.ErrorMessage = "No connection available";
                    return health;
                }

                var database = connection.GetDatabase();
                await database.PingAsync();
                
                health.IsHealthy = true;
                health.Latency = stopwatch.Elapsed;

                // Add additional metrics
                try
                {
                    var server = connection.GetServer(connection.GetEndPoints().First());
                    var info = await server.InfoAsync();
                    
                    // Parse Redis INFO response
                    foreach (var group in info)
                    {
                        if (group.Key == "clients")
                        {
                            health.Metrics["connected_clients"] = group.FirstOrDefault(kvp => kvp.Key == "connected_clients").Value ?? "0";
                        }
                        else if (group.Key == "memory")
                        {
                            health.Metrics["used_memory"] = group.FirstOrDefault(kvp => kvp.Key == "used_memory_human").Value ?? "0";
                        }
                        else if (group.Key == "stats")
                        {
                            health.Metrics["keyspace_hits"] = group.FirstOrDefault(kvp => kvp.Key == "keyspace_hits").Value ?? "0";
                        }
                    }
                }
                catch (Exception infoEx)
                {
                    health.Metrics["info_error"] = infoEx.Message;
                }
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.ErrorMessage = ex.Message;
                health.Latency = stopwatch.Elapsed;
                _logger.LogWarning(ex, "Health check failed for region {Region}", region);
            }

            await _regionSelector.UpdateRegionHealthAsync(region, health);
            return health;
        }

        public Task<IEnumerable<string>> GetAvailableRegionsAsync()
        {
            var availableRegions = _options.Regions
                .Where(r => _connections.ContainsKey(r.Name))
                .Select(r => r.Name)
                .ToList();
            
            return Task.FromResult<IEnumerable<string>>(availableRegions);
        }

        private void InitializeConnections()
        {
            foreach (var region in _options.Regions)
            {
                try
                {
                    var config = ConfigurationOptions.Parse(region.ConnectionString);
                    var connection = ConnectionMultiplexer.Connect(config);
                    _connections[region.Name] = connection;
                    _logger.LogInformation("Connected to region {Region}", region.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to connect to region {Region}", region.Name);
                }
            }
        }

        private IConnectionMultiplexer? GetConnection(string region)
        {
            _connections.TryGetValue(region, out var connection);
            return connection;
        }

        private async void PerformHealthChecks(object? state)
        {
            foreach (var region in _options.Regions)
            {
                try
                {
                    await GetRegionHealthAsync(region.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during health check for region {Region}", region.Name);
                }
            }
        }

        private async void PerformCrossRegionSync(object? state)
        {
            // This is a simplified sync - in practice, you'd track dirty keys
            // or use Redis streams/pub-sub for more efficient replication
            _logger.LogDebug("Performing cross-region sync");
            
            // Implementation would go here - tracking and syncing modified keys
            await Task.CompletedTask;
        }

        private async Task ReplicateToOtherRegionsAsync<T>(string key, T value, TimeSpan expiration, string sourceRegion)
        {
            try
            {
                var availableRegions = await GetAvailableRegionsAsync();
                var targetRegions = await _regionSelector.SelectRegionsForReplicationAsync(key, sourceRegion, availableRegions);

                var replicationTasks = targetRegions.Select(region => SetInRegionAsync(key, value, expiration, region));
                await Task.WhenAll(replicationTasks);

                _logger.LogDebug("Replicated key {Key} from {SourceRegion} to regions {TargetRegions}", 
                    key, sourceRegion, string.Join(", ", targetRegions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to replicate key {Key} from region {SourceRegion}", key, sourceRegion);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _healthCheckTimer?.Dispose();
            _syncTimer?.Dispose();
            _syncSemaphore?.Dispose();

            foreach (var connection in _connections.Values)
            {
                connection?.Dispose();
            }

            _connections.Clear();
            _disposed = true;
        }
    }
}