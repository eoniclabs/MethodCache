using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    /// <summary>
    /// Defines the contract for generating cache keys from method signatures and arguments.
    /// Implementations determine how method calls are mapped to unique cache keys.
    /// </summary>
    /// <remarks>
    /// Key generators are crucial for cache correctness and performance:
    /// - Must produce consistent keys for identical inputs
    /// - Must produce unique keys for different inputs
    /// - Should balance performance vs. readability based on use case
    ///
    /// Built-in implementations:
    /// - FastHashKeyGenerator: ~50ns, binary hash, best for production high-throughput scenarios
    /// - JsonKeyGenerator: ~200ns, human-readable format, best for development/debugging
    /// - MessagePackKeyGenerator: ~100ns, efficient binary serialization, best for complex objects
    /// </remarks>
    /// <example>
    /// Implementing a custom key generator:
    /// <code>
    /// public class CustomKeyGenerator : ICacheKeyGenerator
    /// {
    ///     public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    ///     {
    ///         var version = settings.Version?.ToString() ?? "v1";
    ///         var argsKey = string.Join("_", args.Select(a => a?.ToString() ?? "null"));
    ///         return $"{methodName}_{version}_{argsKey}";
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface ICacheKeyGenerator
    {
        /// <summary>
        /// Generates a unique cache key from method information and arguments.
        /// </summary>
        /// <param name="methodName">The name of the method being cached (e.g., "GetUser", "GetProducts")</param>
        /// <param name="args">The arguments passed to the method, used to differentiate cache entries</param>
        /// <param name="settings">Cache configuration including version and other metadata affecting key generation</param>
        /// <returns>A unique string key identifying this specific method call in the cache</returns>
        /// <remarks>
        /// Implementation guidelines:
        /// - Keys must be deterministic (same inputs → same key)
        /// - Keys must be unique (different inputs → different keys)
        /// - Consider version in key generation to support cache versioning
        /// - Handle null arguments safely
        /// - Be aware of argument types that don't serialize well (e.g., delegates, streams)
        /// </remarks>
        string GenerateKey(string methodName, object[] args, CacheMethodSettings settings);
    }
}
