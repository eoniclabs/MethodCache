using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public interface ICacheWarmingService
    {
        Task StartAsync();
        Task StopAsync();
        Task RegisterWarmupKeyAsync(string key, Func<Task<object>> factory, TimeSpan refreshInterval, string[] tags = null);
        Task UnregisterWarmupKeyAsync(string key);
    }

    public class CacheWarmupEntry
    {
        public string Key { get; }
        public Func<Task<object>> Factory { get; }
        public TimeSpan RefreshInterval { get; }
        public string[] Tags { get; }
        public DateTimeOffset LastWarmedAt { get; set; }
        public DateTimeOffset NextWarmupTime => LastWarmedAt.Add(RefreshInterval);

        public CacheWarmupEntry(string key, Func<Task<object>> factory, TimeSpan refreshInterval, string[] tags)
        {
            Key = key;
            Factory = factory;
            RefreshInterval = refreshInterval;
            Tags = tags ?? Array.Empty<string>();
            LastWarmedAt = DateTimeOffset.MinValue;
        }
    }
}