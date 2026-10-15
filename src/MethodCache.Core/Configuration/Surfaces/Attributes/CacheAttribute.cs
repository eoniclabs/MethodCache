
namespace MethodCache
{
    /// <summary>
    /// Caches the results of an interface method. The source generator writes a decorator for the interface, and
    /// <c>AddMethodCache()</c> wraps the registered implementation in it. On a class member the attribute is ignored
    /// (analyzer MC0001).
    /// </summary>
    /// <remarks>
    /// Keys are built from the method and its arguments. Each setting can be changed without touching the attribute,
    /// and the highest layer that sets it wins: runtime overrides (<c>IRuntimeCacheConfigurator</c>), then
    /// <c>appsettings.json</c> (<c>AddMethodCacheFromConfiguration</c>), then startup code
    /// (<c>AddMethodCache(config => ...)</c>), then this attribute. Defaults and groups fill in what the method leaves
    /// unset. See docs/features/configuration.md.
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
    /// [Cache(Group = "users",
    ///     Duration = "00:30:00",
    ///     Tags = ["users", "user:{userId}"],
    ///     SlidingExpiration = "00:05:00",
    ///     Version = 2)]
    /// Task&lt;UserProfile&gt; GetProfileAsync(int userId);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CacheAttribute : Attribute
    {
        /// <summary>
        /// The group the method belongs to, or null. A group's settings, from <c>config.ForGroup(name)</c> or
        /// <c>appsettings.json</c>, fill in what the method leaves unset.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Gets or sets a positive duration, for example "5m" or "00:05:00".
        /// Determines how long values remain in cache before expiration.
        /// </summary>
        /// <remarks>
        /// Format: a positive invariant decimal with s/m/h/d suffix, or [d.]hh:mm:ss with optional fractional seconds.
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

        /// <summary>Gets or sets the positive delay before retrying a failed load, for example "30s".</summary>
        public string? RetryAfter { get; set; }

        /// <summary>Gets or sets whether caching is enabled. Omission inherits the configured policy.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets whether the method uses distributed caching. Omission inherits the configured policy.</summary>
        public bool Distributed { get; set; } = true;

        /// <summary>
        /// Tags for the entries this method stores, so <c>[CacheInvalidate]</c> or <c>InvalidateByTagsAsync</c> can
        /// remove them. A template such as <c>"product:{id}"</c> is filled in from the parameter with that name; it
        /// takes a parameter name only, not a property path.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Tags = ["products", "product:{id}"])]
        /// Task&lt;Product&gt; GetAsync(int id);
        /// </code>
        /// </example>
        public string[]? Tags { get; set; }

        /// <summary>
        /// A version that is part of every key the method produces. Raise it when the cached result's shape changes,
        /// so values cached by the previous version are ignored. An omitted named argument leaves the version unset.
        /// </summary>
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
        public int Version { get; set; }

        /// <summary>
        /// Caches the method with the explicitly named settings.
        /// </summary>
        public CacheAttribute() { }
    }
}
