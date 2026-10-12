using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache
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
    /// - Tag pattern invalidation
    /// </remarks>
    /// <example>
    /// Basic usage with GetOrCreateAsync:
    /// <code>
    /// var keyGen = new FastHashKeyGenerator();
    /// var user = await cacheManager.GetOrCreateAsync(
    ///     methodName: "GetUser",
    ///     args: new object[] { userId },
    ///     factory: () => database.GetUserAsync(userId),
    ///     policy: policy,
    ///     keyGenerator: keyGen
    /// );
    /// </code>
    /// </example>
    public interface ICacheManager
    {
        // ============= New CacheRuntimePolicy-based methods (preferred) =============

        /// <summary>
        /// Retrieves a cached value or executes the factory function to create and cache a new value.
        /// This is the primary method for caching method results.
        /// </summary>
        /// <typeparam name="T">The type of value being cached</typeparam>
        /// <param name="methodName">Name of the method being cached (used for key generation)</param>
        /// <param name="args">Method arguments (used for key generation to differentiate cache entries)</param>
        /// <param name="factory">Factory function to execute if cache miss occurs</param>
        /// <param name="policy">Runtime policy containing cache configuration and options</param>
        /// <param name="keyGenerator">Strategy for generating cache keys from method name and arguments</param>
        /// <returns>The cached or newly created value</returns>
        Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator);

        /// <summary>
        /// Attempts to retrieve a value from the cache without executing a factory function.
        /// This is a fast, read-only path that bypasses factory execution and reduces overhead.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="methodName">Method name for key generation</param>
        /// <param name="args">Method arguments for key generation</param>
        /// <param name="policy">Runtime policy for key generation</param>
        /// <param name="keyGenerator">Key generator instance</param>
        /// <returns>The cached value if found, or default(T) if not in cache</returns>
        ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator);

        /// <summary>Reads a fresh memory entry once and reports presence separately from its value, including null.</summary>
        /// <typeparam name="T">The cached value type.</typeparam>
        /// <param name="methodName">The method name used to generate its key.</param>
        /// <param name="args">The method arguments.</param>
        /// <param name="policy">The method's runtime policy.</param>
        /// <param name="keyGenerator">The cache key generator.</param>
        /// <returns>A hit with the stored value, or a miss. Does not start a load or refresh.</returns>
        ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator);

        /// <summary>
        /// Ultra-fast cache lookup that bypasses key generation, policy checks, and statistics.
        /// Use this when you have a pre-computed cache key and need minimal latency.
        /// This method only performs basic expiration checking.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key (must match the key used during cache population)</param>
        /// <returns>The cached value if found and not expired, or default(T) if not in cache</returns>
        /// <remarks>
        /// This method is optimized for maximum performance:
        /// - Skips key generation (uses pre-computed key)
        /// - Skips policy lookup and construction
        /// - Minimal expiration checking
        /// - No statistics tracking (if disabled)
        /// - No LRU updates (if disabled)
        /// Use this for hot paths where every microsecond counts.
        /// </remarks>
        ValueTask<T?> TryGetFastAsync<T>(string cacheKey);

        /// <summary>
        /// Tries to retrieve a cached value synchronously using the ultra-fast path.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key</param>
        /// <param name="value">The cached value if found</param>
        /// <returns>True if the value exists and is not expired</returns>
        bool TryGetFast<T>(string cacheKey, out T? value)
        {
            var fast = TryGetFastAsync<T>(cacheKey);
            if (!fast.IsCompletedSuccessfully)
            {
                value = fast.AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            else
            {
                value = fast.Result;
            }

            return value != null && !EqualityComparer<T>.Default.Equals(value, default);
        }

        /// <summary>
        /// Like <see cref="TryGetFast{T}(string, out T)"/>, but returns the hit as a completed task for <see cref="Task{TResult}"/>
        /// methods. Implementations can hand out the same task for every hit on an entry, so a hit does not allocate.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key</param>
        /// <param name="task">A completed task holding the cached value, if found</param>
        /// <returns>True if the value exists and is not expired</returns>
        bool TryGetFastTask<T>(string cacheKey, [NotNullWhen(true)] out Task<T>? task)
        {
            if (TryGetFast<T>(cacheKey, out var value))
            {
                task = Task.FromResult(value!);
                return true;
            }

            task = null;
            return false;
        }

        /// <summary>
        /// Like <see cref="TryGetFast{T}(string, out T)"/>, for a key held in a buffer such as one written by
        /// <see cref="MethodCache.Infrastructure.MethodCacheKeyBuilder"/>. The built-in memory cache looks it up without
        /// allocating on .NET 9 and later; the default implementation converts the key to a string.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key</param>
        /// <param name="value">The cached value if found</param>
        /// <returns>True if the value exists and is not expired</returns>
        bool TryGetFast<T>(ReadOnlySpan<char> cacheKey, out T? value) => TryGetFast<T>(cacheKey.ToString(), out value);

        /// <summary>
        /// Like <see cref="TryGetFastTask{T}(string, out Task{T})"/>, for a key held in a buffer such as one written by
        /// <see cref="MethodCache.Infrastructure.MethodCacheKeyBuilder"/>.
        /// </summary>
        /// <typeparam name="T">Type of cached value</typeparam>
        /// <param name="cacheKey">Pre-computed cache key</param>
        /// <param name="task">A completed task holding the cached value, if found</param>
        /// <returns>True if the value exists and is not expired</returns>
        bool TryGetFastTask<T>(ReadOnlySpan<char> cacheKey, [NotNullWhen(true)] out Task<T>? task)
            => TryGetFastTask<T>(cacheKey.ToString(), out task);

        /// <summary>
        /// Retrieves a cached value or executes the factory function using a pre-computed cache key.
        /// This fast-path method is optimized for methods with simple parameters where the cache key
        /// can be computed inline without heavyweight serialization.
        /// </summary>
        /// <typeparam name="T">The type of value being cached</typeparam>
        /// <param name="cacheKey">Pre-computed cache key (must be deterministic and unique for the cached method+args)</param>
        /// <param name="methodName">Name of the method being cached (for metrics/diagnostics)</param>
        /// <param name="factory">Factory function to execute if cache miss occurs</param>
        /// <param name="policy">Runtime policy containing cache configuration and options</param>
        /// <param name="cancellationToken">
        /// The caller's token, which <paramref name="factory"/> also observes. Cancelling it cancels this caller's
        /// wait; a load it started is cancelled too, and callers that joined that load start their own.
        /// </param>
        /// <returns>The cached or newly created value</returns>
        /// <remarks>
        /// This overload is designed for source-generated code that can compute cache keys inline
        /// for simple parameter types (string, int, Guid, etc.) without MessagePack serialization overhead.
        /// Use this for maximum performance when you can deterministically generate cache keys.
        /// </remarks>
        Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default);

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
        ///     MethodCacheKeys.For(typeof(IUserService), "GetUser", MethodCacheKeys.Part(123)),
        ///     MethodCacheKeys.For(typeof(IUserService), "GetUser", MethodCacheKeys.Part(456))
        /// );
        /// </code>
        /// </example>
        Task InvalidateByKeysAsync(params string[] keys);

        /// <summary>
        /// Invalidates cache entries whose tags match a wildcard pattern, where <c>*</c> matches any run of characters
        /// and <c>?</c> matches one character.
        /// </summary>
        /// <param name="pattern">Pattern to match tags (e.g., "user:*" or "tenant:?:orders")</param>
        /// <returns>A task representing the asynchronous invalidation operation</returns>
        /// <exception cref="NotSupportedException">A distributed cache is configured; invalidate the exact tags instead.</exception>
        /// <example>
        /// <code>
        /// // Invalidate all entries tagged for any user
        /// await cacheManager.InvalidateByTagPatternAsync("user:*");
        ///
        /// // Invalidate all caches for a specific tenant
        /// await cacheManager.InvalidateByTagPatternAsync($"tenant:{tenantId}:*");
        /// </code>
        /// </example>
        Task InvalidateByTagPatternAsync(string pattern);
    }
}
