using System.Threading.Tasks;
using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    public interface ICacheManager
    {
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, System.Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent);
        Task InvalidateByTagsAsync(params string[] tags);
        Task InvalidateByKeysAsync(params string[] keys);
        Task InvalidateByTagPatternAsync(string pattern);
        
        /// <summary>
        /// Directly tries to get a value from the cache without factory execution.
        /// This is a fast read-only path that bypasses all factory plumbing and metrics overhead.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="methodName">Method name for key generation</param>
        /// <param name="args">Method arguments for key generation</param>
        /// <param name="settings">Cache settings for key generation</param>
        /// <param name="keyGenerator">Key generator instance</param>
        /// <returns>The cached value or default(T) if not found</returns>
        ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator);
    }
}
