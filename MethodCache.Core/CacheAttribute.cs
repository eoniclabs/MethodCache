
using System;

namespace MethodCache.Core
{
    /// <summary>
    /// Marks a method for automatic caching using MethodCache source generation.
    /// Apply this attribute to interface methods or virtual methods to enable caching without manual implementation.
    /// </summary>
    /// <remarks>
    /// The CacheAttribute enables declarative caching with compile-time source generation.
    /// Configuration can be overridden at runtime using fluent API, JSON/YAML config, or runtime overrides.
    ///
    /// Configuration precedence (highest to lowest):
    /// 1. Runtime overrides (IRuntimeCacheConfigurator)
    /// 2. Startup configuration (fluent API or config files)
    /// 3. Attribute values
    /// </remarks>
    /// <example>
    /// Basic usage with default settings:
    /// <code>
    /// public interface IUserService
    /// {
    ///     [Cache]
    ///     Task&lt;User&gt; GetUserAsync(int userId);
    /// }
    /// </code>
    ///
    /// Advanced usage with full configuration:
    /// <code>
    /// [Cache("users",
    ///     Duration = "00:30:00",
    ///     Tags = new[] { "users", "profiles" },
    ///     Version = 2,
    ///     KeyGeneratorType = typeof(FastHashKeyGenerator),
    ///     RequireIdempotent = true)]
    /// Task&lt;UserProfile&gt; GetProfileAsync(int userId);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CacheAttribute : Attribute
    {
        /// <summary>
        /// Gets the cache group name for organizing related cache entries.
        /// Group names allow bulk configuration and invalidation of related cache entries.
        /// </summary>
        /// <remarks>
        /// Examples: "users", "products", "reports"
        /// </remarks>
        public string? GroupName { get; }

        /// <summary>
        /// Gets or sets whether the cached method must be idempotent.
        /// When true, enforces that the method produces the same output for the same inputs.
        /// </summary>
        /// <remarks>
        /// Set to true for read operations (GET, queries).
        /// Set to false for operations with side effects (write operations).
        /// Default: false
        /// </remarks>
        public bool RequireIdempotent { get; set; }

        /// <summary>
        /// Gets or sets the cache duration as a TimeSpan string.
        /// Determines how long values remain in cache before expiration.
        /// </summary>
        /// <remarks>
        /// Format: "HH:MM:SS" or use TimeSpan.Parse-compatible strings.
        /// Examples: "00:05:00" (5 minutes), "01:00:00" (1 hour), "1.00:00:00" (1 day)
        /// If not specified, uses the default duration from global configuration.
        /// </remarks>
        public string? Duration { get; set; }

        /// <summary>
        /// Gets or sets tags for cache entry categorization and bulk invalidation.
        /// Tags enable invalidating multiple related cache entries in a single operation.
        /// </summary>
        /// <remarks>
        /// Use tags to group related data for invalidation:
        /// - Entity type tags: "users", "products", "orders"
        /// - Specific entity tags: "user:123", "product:456"
        /// - Feature tags: "catalog", "reports", "analytics"
        /// </remarks>
        /// <example>
        /// <code>
        /// [Cache(Tags = new[] { "products", "product:123", "catalog" })]
        /// </code>
        /// </example>
        public string[]? Tags { get; set; }

        /// <summary>
        /// Gets or sets the cache version for versioned cache keys.
        /// Incrementing the version invalidates all previous cache entries for this method.
        /// </summary>
        /// <remarks>
        /// Use versioning when:
        /// - Data structure changes require cache invalidation
        /// - Business logic changes affect cached results
        /// - You need to invalidate all cache entries without pattern matching
        ///
        /// Default: -1 (no versioning)
        /// </remarks>
        /// <example>
        /// <code>
        /// // Version 1
        /// [Cache(Version = 1)]
        /// Task&lt;UserDto&gt; GetUserAsync(int id);
        ///
        /// // After UserDto schema change, increment version to invalidate old caches
        /// [Cache(Version = 2)]
        /// Task&lt;UserDto&gt; GetUserAsync(int id);
        /// </code>
        /// </example>
        public int Version { get; set; } = -1;

        /// <summary>
        /// Gets or sets the key generator type for creating cache keys from method arguments.
        /// Key generators determine how cache keys are built from method name and parameters.
        /// </summary>
        /// <remarks>
        /// Built-in key generators:
        /// - FastHashKeyGenerator: Fastest performance, binary hash (default for production)
        /// - JsonKeyGenerator: Human-readable keys for debugging
        /// - MessagePackKeyGenerator: Efficient for complex objects
        ///
        /// Custom key generators must implement ICacheKeyGenerator.
        /// </remarks>
        /// <example>
        /// <code>
        /// [Cache(KeyGeneratorType = typeof(JsonKeyGenerator))]  // Debug-friendly
        /// [Cache(KeyGeneratorType = typeof(FastHashKeyGenerator))]  // Production
        /// [Cache(KeyGeneratorType = typeof(CustomKeyGenerator))]  // Custom logic
        /// </code>
        /// </example>
        public Type? KeyGeneratorType { get; set; }

        /// <summary>
        /// Initializes a new instance of the CacheAttribute.
        /// </summary>
        /// <param name="groupName">Optional group name for organizing related cache entries</param>
        public CacheAttribute(string? groupName = null)
        {
            GroupName = groupName;
            RequireIdempotent = false; // Default to false
        }
    }
}
