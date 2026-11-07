
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
    /// <c>appsettings.json</c> (<c>mc.FromConfiguration(configuration)</c>), then startup code
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
    ///     Duration = "30m",
    ///     Tags = ["users", "user:{userId}"],
    ///     SlidingExpiration = "5m",
    ///     Version = 2)]
    /// Task&lt;UserProfile&gt; GetProfileAsync(int userId);
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CacheAttribute : Attribute
    {
        /// <summary>
        /// The group the method belongs to, or null. A group's settings, from <c>mc.Group(name, ...)</c> or the
        /// <c>"Groups"</c> section of <c>appsettings.json</c>, fill in what the method leaves unset.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Gets or sets a positive duration, for example "5m" or "1h".
        /// Determines how long values remain in cache before expiration.
        /// </summary>
        /// <remarks>
        /// Format: a positive number with a unit, s, m, h or d, and a "." for decimals: "30s", "5m", "1.5h", "7d".
        /// A TimeSpan such as "00:05:00" is an error (MC0004).
        /// If not specified, uses the default duration from global configuration.
        /// </remarks>
        public string? Duration { get; set; }

        /// <summary>
        /// Gets or sets a sliding expiration, a duration such as "5m". Each read extends the entry by this much, but
        /// never beyond <see cref="Duration"/>. <c>"off"</c> turns it off for this method when it is on for all of them.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "1h", SlidingExpiration = "5m")]  // Dropped after 5 idle minutes, at most 1 hour old
        /// </code>
        /// </example>
        public string? SlidingExpiration { get; set; }

        /// <summary>
        /// Gets or sets a refresh-ahead window, a duration such as "1m". A read within this window before the entry
        /// expires returns the cached value and refreshes it in the background, so callers do not wait for the method.
        /// <c>"off"</c> turns it off for this method when it is on for all of them.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "10m", RefreshAhead = "1m")]  // Reloaded in the background during the last minute
        /// </code>
        /// </example>
        public string? RefreshAhead { get; set; }

        /// <summary>
        /// Gets or sets how long an expired value is kept and returned when calling the method again throws, as a
        /// duration such as "1h", or <c>"off"</c> for a method that must never return old data when fail-safe is on for all
        /// methods. Unset uses the group or the central setting (<c>mc.Defaults(o =&gt; o.WithFailSafe(...))</c>).
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "5m", FailSafe = "1h")]  // Up to an hour-old value if the database is down
        /// [Cache(FailSafe = "off")]                              // An account balance: an error beats an old value
        /// </code>
        /// </example>
        public string? FailSafe { get; set; }

        /// <summary>
        /// Gets or sets, as a duration such as "0.2s", how long the caller waits for the method when a fail-safe value exists.
        /// A slower call returns the fail-safe value and finishes in the background, updating the cache.
        /// </summary>
        /// <example>
        /// <code>
        /// [Cache(Duration = "5m", FailSafe = "1h", FailSafeTimeout = "0.2s")]
        /// </code>
        /// </example>
        public string? FailSafeTimeout { get; set; }

        /// <summary>Gets or sets how long a served fail-safe value is reused before the method is called again, for example "30s".</summary>
        public string? FailSafeRetryAfter { get; set; }

        /// <summary>Gets or sets whether caching is enabled. Omission inherits the configured policy.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets whether the method uses distributed caching. Omission inherits the configured policy.</summary>
        public bool Distributed { get; set; } = true;

        /// <summary>
        /// Tags for the entries this method stores, so <c>[CacheInvalidate]</c> or <c>ICacheManager.InvalidateTagsAsync</c> can
        /// remove them. A template such as <c>"product:{id}"</c> is filled in from the parameter with that name; it
        /// takes a parameter name only, not a property path.
        /// </summary>
        /// <remarks>
        /// Arguments are written in one canonical way, the same on every machine: a <see cref="System.Guid"/> as 32 digits
        /// without hyphens, an enum as its number, <c>true</c>/<c>false</c> in lower case, numbers and dates in the invariant
        /// culture, and <c>:</c> and <c>\</c> escaped in strings (<c>"gemma3:4b"</c> becomes <c>gemma3\:4b</c>). To invalidate
        /// from code that doesn't call a <c>[CacheInvalidate]</c> method, such as an endpoint that writes to the database
        /// directly, get the tag from <see cref="IMethodCacheKeys.Tag{TService}"/> instead of building the string yourself.
        /// A <c>const</c> on the interface lets the attribute and that call share the template.
        /// </remarks>
        /// <example>
        /// <code>
        /// [Cache(Tags = ["products", "product:{id}"])]
        /// Task&lt;Product&gt; GetAsync(int id);
        ///
        /// // After the endpoint saved product 42
        /// await cache.InvalidateTagAsync(keys.Tag&lt;IProductService&gt;(s =&gt; s.GetAsync(42), "product:{id}"));
        /// </code>
        /// </example>
        public string[]? Tags { get; set; }

        /// <summary>
        /// The tags in <see cref="Tags"/> that another application or process invalidates, such as a worker that imports
        /// the data. Analyzer MC0028 and <c>TagCoverage</c> in MethodCache.Testing count them as invalidated, on every
        /// method that has the tag. Caching itself doesn't read this setting.
        /// </summary>
        /// <remarks>
        /// Write each tag as it is written in <see cref="Tags"/>. One that matches none of them is warning MC0029.
        /// </remarks>
        /// <example>
        /// <code>
        /// // The import worker invalidates "recipes"; this application invalidates "recipe:{id}"
        /// [Cache(Tags = ["recipes", "recipe:{id}"], InvalidatedElsewhere = ["recipes"])]
        /// Task&lt;Recipe&gt; GetAsync(int id);
        /// </code>
        /// </example>
        public string[]? InvalidatedElsewhere { get; set; }

        /// <summary>
        /// The tags in <see cref="Tags"/> that no code invalidates when data changes: an operator clears them by hand,
        /// such as with <c>POST invalidate</c> of <c>MapMethodCacheAdmin</c>, and otherwise their entries expire.
        /// Analyzer MC0028 and <c>TagCoverage</c> in MethodCache.Testing count them as invalidated, on every method that
        /// has the tag, without looking for an invalidation. Caching itself doesn't read this setting.
        /// </summary>
        /// <remarks>
        /// Write each tag as it is written in <see cref="Tags"/>. One that matches none of them is warning MC0029. For a
        /// tag that another application or process invalidates, use <see cref="InvalidatedElsewhere"/>, which
        /// <c>TagCoverage</c> checks.
        /// </remarks>
        /// <example>
        /// <code>
        /// // The catalog changes a few times a year; an operator clears "food-catalog" after an update
        /// [Cache(Duration = "1d", Tags = ["food-catalog"], InvalidatedManually = ["food-catalog"])]
        /// Task&lt;IReadOnlyList&lt;Food&gt;&gt; SearchAsync(string text);
        /// </code>
        /// </example>
        public string[]? InvalidatedManually { get; set; }

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
