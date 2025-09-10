using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public interface IRegionSelector
    {
        Task<string> SelectRegionForReadAsync(string key, IEnumerable<string> availableRegions);
        Task<string> SelectRegionForWriteAsync(string key, IEnumerable<string> availableRegions);
        Task<IEnumerable<string>> SelectRegionsForReplicationAsync(string key, string sourceRegion, IEnumerable<string> availableRegions);
        Task UpdateRegionHealthAsync(string region, RegionHealthStatus health);
        Task<RegionHealthStatus?> GetRegionHealthAsync(string region);
        Task<IEnumerable<string>> GetHealthyRegionsAsync();
    }

    public class RegionSelector : IRegionSelector
    {
        private readonly MultiRegionOptions _options;
        private readonly Dictionary<string, RegionHealthStatus> _regionHealth;
        private readonly object _lockObject = new object();

        public RegionSelector(MultiRegionOptions options)
        {
            _options = options;
            _regionHealth = new Dictionary<string, RegionHealthStatus>();
        }

        public async Task<string> SelectRegionForReadAsync(string key, IEnumerable<string> availableRegions)
        {
            var healthyRegions = await GetHealthyRegionsAsync();
            var validRegions = availableRegions.Intersect(healthyRegions).ToList();

            if (!validRegions.Any())
                return availableRegions.First(); // Fallback to any available region

            return _options.FailoverStrategy switch
            {
                RegionFailoverStrategy.PriorityBased => SelectByPriority(validRegions),
                RegionFailoverStrategy.LatencyBased => SelectByLatency(validRegions),
                RegionFailoverStrategy.RoundRobin => SelectRoundRobin(validRegions, key),
                RegionFailoverStrategy.Sticky => SelectSticky(validRegions, key),
                _ => validRegions.First()
            };
        }

        public async Task<string> SelectRegionForWriteAsync(string key, IEnumerable<string> availableRegions)
        {
            // For writes, prefer the primary region if available and healthy
            if (!string.IsNullOrEmpty(_options.PrimaryRegion) && 
                availableRegions.Contains(_options.PrimaryRegion))
            {
                var primaryHealth = await GetRegionHealthAsync(_options.PrimaryRegion);
                if (primaryHealth?.IsHealthy == true)
                    return _options.PrimaryRegion;
            }

            // Fallback to read selection logic
            return await SelectRegionForReadAsync(key, availableRegions);
        }

        public async Task<IEnumerable<string>> SelectRegionsForReplicationAsync(string key, string sourceRegion, IEnumerable<string> availableRegions)
        {
            var healthyRegions = await GetHealthyRegionsAsync();
            var targetRegions = availableRegions
                .Except(new[] { sourceRegion })
                .Intersect(healthyRegions)
                .ToList();

            // Limit concurrent replications
            return targetRegions.Take(_options.MaxConcurrentSyncs);
        }

        public Task UpdateRegionHealthAsync(string region, RegionHealthStatus health)
        {
            lock (_lockObject)
            {
                _regionHealth[region] = health;
            }
            return Task.CompletedTask;
        }

        public Task<RegionHealthStatus?> GetRegionHealthAsync(string region)
        {
            lock (_lockObject)
            {
                _regionHealth.TryGetValue(region, out var health);
                return Task.FromResult(health);
            }
        }

        public Task<IEnumerable<string>> GetHealthyRegionsAsync()
        {
            lock (_lockObject)
            {
                var healthy = _regionHealth
                    .Where(kvp => kvp.Value.IsHealthy)
                    .Select(kvp => kvp.Key)
                    .ToList();
                return Task.FromResult<IEnumerable<string>>(healthy);
            }
        }

        private string SelectByPriority(IEnumerable<string> regions)
        {
            var regionConfigs = _options.Regions.Where(r => regions.Contains(r.Name));
            return regionConfigs.OrderByDescending(r => r.Priority).First().Name;
        }

        private string SelectByLatency(IEnumerable<string> regions)
        {
            lock (_lockObject)
            {
                return regions
                    .Where(r => _regionHealth.ContainsKey(r))
                    .OrderBy(r => _regionHealth[r].Latency)
                    .FirstOrDefault() ?? regions.First();
            }
        }

        private string SelectRoundRobin(IEnumerable<string> regions, string key)
        {
            var regionList = regions.ToList();
            var index = Math.Abs(key.GetHashCode()) % regionList.Count;
            return regionList[index];
        }

        private string SelectSticky(IEnumerable<string> regions, string key)
        {
            // Use consistent hashing to stick keys to regions
            var regionList = regions.OrderBy(r => r).ToList();
            var index = Math.Abs(key.GetHashCode()) % regionList.Count;
            return regionList[index];
        }
    }
}