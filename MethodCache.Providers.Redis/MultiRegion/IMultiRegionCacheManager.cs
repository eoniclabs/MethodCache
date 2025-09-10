using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.MultiRegion
{
    public interface IMultiRegionCacheManager
    {
        Task<T?> GetFromRegionAsync<T>(string key, string region);
        Task SetInRegionAsync<T>(string key, T value, TimeSpan expiration, string region);
        Task InvalidateInRegionAsync(string key, string region);
        Task InvalidateGloballyAsync(string key);
        Task<Dictionary<string, T?>> GetFromMultipleRegionsAsync<T>(string key, IEnumerable<string> regions);
        Task SyncToRegionAsync(string key, string sourceRegion, string targetRegion);
        Task<RegionHealthStatus> GetRegionHealthAsync(string region);
        Task<IEnumerable<string>> GetAvailableRegionsAsync();
    }

    public class RegionHealthStatus
    {
        public string Region { get; set; } = string.Empty;
        public bool IsHealthy { get; set; }
        public TimeSpan Latency { get; set; }
        public DateTime LastChecked { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new();
    }
}