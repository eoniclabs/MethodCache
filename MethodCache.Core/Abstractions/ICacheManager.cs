using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;

namespace MethodCache.Core
{
    /// <summary>
    /// Manages caching operations with support for L1 (in-memory) and L2 (distributed) cache layers.
    /// Provides methods for retrieving cached values, invalidating cache entries, and managing cache lifecycle.
    /// </summary>
    /// <remarks>
    /// ICacheManager is the core interface for all caching operations in MethodCache.
    /// It supports:
    /// - Multi-layer caching (L1/L2)
    /// - Tag-based invalidation for bulk cache clearing
    /// - Custom key generation strategies
    /// - Pattern-based cache key matching
    /// </remarks>
    /// <example>
    /// Basic usage with GetOrCreateAsync:
    /// <code>
    /// var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(10) };
    /// var keyGen = new FastHashKeyGenerator();
    /// var user = await cacheManager.GetOrCreateAsync(
    ///     methodName: "GetUser",
    ///     args: new object[] { userId },
    ///     factory: () => database.GetUserAsync(userId),
    ///     settings: settings,
    ///     keyGenerator: keyGen,
    ///     requireIdempotent: true
    /// );
    /// </code>
    /// </example>
    public interface ICacheManager
    {
        /// <summary>
        /// Retrieves a cached value or executes the factory function to create and cache a new value.
        /// This is the primary method for caching method results.
        /// </summary>
        /// <typeparam name="T">The type of value being cached</typeparam>
        /// <param name="methodName">Name of the method being cached (used for key generation)</param>
        /// <param name="args">Method arguments (used for key generation to differentiate cache entries)</param>
        /// <param name="factory">Factory function to execute if cache miss occurs</param>
        /// <param name="settings">Cache configuration including duration, tags, and versioning</param>
        /// <param name="keyGenerator">Strategy for generating cache keys from method name and arguments</param>
        /// <param name="requireIdempotent">If true, enforces that the cached method is idempotent (same inputs always produce same outputs)</param>
        /// <returns>The cached or newly created value</returns>
        /// <example>
        /// <code>
        /// var product = await cacheManager.GetOrCreateAsync(
        ///     methodName: "GetProduct",
        ///     args: new object[] { productId, includeDetails },
        ///     factory: () => productService.GetProductAsync(productId, includeDetails),
        ///     settings: new CacheMethodSettings {
        ///         Duration = TimeSpan.FromMinutes(30),
        ///         Tags = new[] { "products", $"product:{productId}" }
        ///     },
        ///     keyGenerator: new JsonKeyGenerator(),
        ///     requireIdempotent: true
        /// );
        /// </code>
        /// </example>
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, System.Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent);

        /// <summary>
        /// Retrieves a cached value using a policy descriptor produced by the policy pipeline.
        /// Default implementation bridges to the legacy <see cref="CacheMethodSettings"/> path.
        /// </summary>
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, System.Func<Task<T>> factory, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
            => GetOrCreateAsync(methodName, args, factory, descriptor.ToCacheMethodSettings(), keyGenerator, descriptor.RequireIdempotent);

        /// <summary>
        /// Invalidates all cache entries associated with the specified tags.
        /// Use this for bulk invalidation when related data changes.
        /// </summary>
        /// <param name="tags">One or more tags identifying cache entries to invalidate</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// // Invalidate all product-related caches
        /// await cacheManager.InvalidateByTagsAsync("products", "catalog");
        ///
        /// // Invalidate caches for a specific user
        /// await cacheManager.InvalidateByTagsAsync($"user:{userId}");
        /// </code>
        /// </example>
        Task InvalidateByTagsAsync(params string[] tags);

        /// <summary>
        /// Invalidates cache entries with the specified exact keys.
        /// Use this for precise invalidation of specific cache entries.
        /// </summary>
        /// <param name="keys">One or more cache keys to invalidate</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// await cacheManager.InvalidateByKeysAsync(
        ///     "GetUser_123",
        ///     "GetUser_456"
        /// );
        /// </code>
        /// </example>
        Task InvalidateByKeysAsync(params string[] keys);

        /// <summary>
        /// Invalidates cache entries matching a wildcard pattern.
        /// Supported patterns depend on the cache provider (e.g., Redis supports glob patterns).
        /// </summary>
        /// <param name="pattern">Pattern to match cache keys (e.g., "GetUser*" or "user:*:profile")</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <example>
        /// <code>
        /// // Invalidate all user-related caches
        /// await cacheManager.InvalidateByTagPatternAsync("GetUser*");
        ///
        /// // Invalidate all caches for a specific tenant
        /// await cacheManager.InvalidateByTagPatternAsync($"tenant:{tenantId}:*");
        /// </code>
        /// </example>
        Task InvalidateByTagPatternAsync(string pattern);

        /// <summary>
        /// Attempts to retrieve a value from the cache without executing a factory function.
        /// This is a fast, read-only path that bypasses factory execution and reduces overhead.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="methodName">Method name for key generation</param>
        /// <param name="args">Method arguments for key generation</param>
        /// <param name="settings">Cache settings for key generation</param>
        /// <param name="keyGenerator">Key generator instance</param>
        /// <returns>The cached value if found, or default(T) if not in cache</returns>
        /// <example>
        /// <code>
        /// var cachedUser = await cacheManager.TryGetAsync&lt;User&gt;(
        ///     methodName: "GetUser",
        ///     args: new object[] { userId },
        ///     settings: settings,
        ///     keyGenerator: new FastHashKeyGenerator()
        /// );
        ///
        /// if (cachedUser != null)
        /// {
        ///     // Use cached value
        /// }
        /// else
        /// {
        ///     // Not in cache, need to fetch from source
        /// }
        /// </code>
        /// </example>
        ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator);

        /// <summary>
        /// Attempts to retrieve a cached value using a policy descriptor produced by the policy pipeline.
        /// Default implementation bridges to the legacy <see cref="CacheMethodSettings"/> path.
        /// </summary>
        ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
            => TryGetAsync<T>(methodName, args, descriptor.ToCacheMethodSettings(), keyGenerator);
    }
}
