using MethodCache.Infrastructure;

namespace MethodCache.Extensibility
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
    /// Generated decorators build keys for simple arguments inline (see <see cref="MethodCacheKeys"/>) and only call
    /// a key generator for other arguments, passing the method's key prefix as <c>methodName</c>.
    ///
    /// Built-in implementations:
    /// - FastHashKeyGenerator (the default): 128-bit hash of a canonical binary form
    /// - JsonKeyGenerator: human-readable format, useful for debugging
    /// - MessagePackKeyGenerator (MethodCache.Serialization.MessagePack package): SHA-256 of the MessagePack form
    /// </remarks>
    /// <example>
    /// Implementing a custom key generator:
    /// <code>
    /// public class CustomKeyGenerator : ICacheKeyGenerator
    /// {
    ///     public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
    ///     {
    ///         var version = policy.Version?.ToString() ?? "v1";
    ///         var argsKey = string.Join("_", args.Select(a => a?.ToString() ?? "null"));
    ///         return $"{methodName}_{version}_{argsKey}";
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface ICacheKeyGenerator
    {
        /// <summary>
        /// Generates a unique cache key from method information and arguments using a runtime policy.
        /// </summary>
        /// <param name="methodName">The name of the method being cached (e.g., "GetUser", "GetProducts")</param>
        /// <param name="args">The arguments passed to the method, used to differentiate cache entries</param>
        /// <param name="policy">Runtime policy containing version and other metadata for key generation</param>
        /// <returns>A unique string key identifying this specific method call in the cache</returns>
        /// <remarks>
        /// Implementation guidelines:
        /// - Keys must be deterministic (same inputs → same key)
        /// - Keys must be unique (different inputs → different keys)
        /// - Consider policy.Version in key generation to support cache versioning
        /// - Handle null arguments safely
        /// - Be aware of argument types that don't serialize well (e.g., delegates, streams)
        /// </remarks>
        string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy);
    }
}
