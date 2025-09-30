using System.Threading.Tasks;

namespace MethodCache.Core.Storage
{
    /// <summary>
    /// Service for proactively warming cache entries before they expire
    /// </summary>
    public interface ICacheWarmingService
    {
        /// <summary>
        /// Starts the cache warming service
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Stops the cache warming service
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Registers a cache key for automatic warming
        /// </summary>
        /// <param name="key">The cache key to warm</param>
        /// <param name="factory">Function to generate fresh data</param>
        /// <param name="refreshInterval">How often to refresh the data</param>
        /// <param name="tags">Optional tags to associate with the cached data</param>
        Task RegisterWarmupKeyAsync(string key, Func<Task<object>> factory, TimeSpan refreshInterval, string[]? tags = null);

        /// <summary>
        /// Unregisters a cache key from automatic warming
        /// </summary>
        /// <param name="key">The cache key to stop warming</param>
        Task UnregisterWarmupKeyAsync(string key);
    }

    /// <summary>
    /// Represents a cache entry registered for warming
    /// </summary>
    public class CacheWarmupEntry
    {
        /// <summary>
        /// The cache key
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Function to generate fresh data
        /// </summary>
        public Func<Task<object>> Factory { get; }

        /// <summary>
        /// How often to refresh the data
        /// </summary>
        public TimeSpan RefreshInterval { get; }

        /// <summary>
        /// Tags associated with the cached data
        /// </summary>
        public string[] Tags { get; }

        /// <summary>
        /// When this entry was last warmed
        /// </summary>
        public DateTimeOffset LastWarmedAt { get; set; }

        /// <summary>
        /// When this entry should next be warmed
        /// </summary>
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