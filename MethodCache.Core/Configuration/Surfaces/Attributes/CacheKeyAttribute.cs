using System;

namespace MethodCache.Core
{
    /// <summary>
    /// Configures how a method parameter is used in cache key generation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class CacheKeyAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets whether this parameter should be used as the raw cache key,
        /// skipping the method name prefix and other parameters.
        /// </summary>
        /// <remarks>
        /// Use this when you want full control over the cache key string.
        /// WARNING: This increases the risk of cache collisions if the same key is used by different methods/types.
        /// Ensure the key is globally unique within the cache namespace.
        /// </remarks>
        public bool UseAsRawKey { get; set; }
    }
}
