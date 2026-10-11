
namespace MethodCache
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
    ///     SlidingExpiration = "00:05:00",
    ///     Version = 2)]
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
        /// Gets or sets a sliding expiration as a TimeSpan string. Each read extends the entry by this much, but
        /// never beyond <see cref="Duration"/>.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "01:00:00", SlidingExpiration = "00:05:00")]  // Dropped after 5 idle minutes, at most 1 hour old
        /// </code>
        /// </example>
        public string? SlidingExpiration { get; set; }

        /// <summary>
        /// Gets or sets a refresh-ahead window as a TimeSpan string. A read within this window before the entry
        /// expires returns the cached value and refreshes it in the background, so callers do not wait for the method.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "00:10:00", RefreshAhead = "00:01:00")]  // Reloaded in the background during the last minute
        /// </code>
        /// </example>
        public string? RefreshAhead { get; set; }

        /// <summary>
        /// Gets or sets how long an expired value is kept and returned when calling the method again throws, as a
        /// TimeSpan string, or <c>"off"</c> for a method that must never return old data when fail-safe is on for all
        /// methods. Unset uses the group or the central setting (<c>config.FailSafe(...)</c>).
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "00:05:00", FailSafe = "01:00:00")]  // Up to an hour-old value if the database is down
        /// [Cache(FailSafe = "off")]                              // An account balance: an error beats an old value
        /// </code>
        /// </example>
        public string? FailSafe { get; set; }

        /// <summary>
        /// Gets or sets, as a TimeSpan string, how long the caller waits for the method when a fail-safe value exists.
        /// A slower call returns the fail-safe value and finishes in the background, updating the cache.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "00:05:00", FailSafe = "01:00:00", FailSafeTimeout = "00:00:00.200")]
        /// </code>
        /// </example>
        public string? FailSafeTimeout { get; set; }

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
        /// Initializes a new instance of the CacheAttribute.
        /// </summary>
        /// <param name="groupName">Optional group name for organizing related cache entries</param>
        public CacheAttribute(string? groupName = null)
        {
            GroupName = groupName;
        }
    }
}
