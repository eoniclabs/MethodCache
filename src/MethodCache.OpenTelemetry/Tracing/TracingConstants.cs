using System.Diagnostics;

namespace MethodCache.OpenTelemetry;

/// <summary>
/// Names of the activity source, span operations, span attributes and baggage keys MethodCache uses for tracing.
/// </summary>
public static class TracingConstants
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> that MethodCache's spans come from.
    /// </summary>
    public const string ActivitySourceName = "MethodCache";

    /// <summary>
    /// The version of the activity source.
    /// </summary>
    public const string ActivitySourceVersion = "1.0.0";

    /// <summary>
    /// Span names.
    /// </summary>
    public static class Operations
    {
        /// <summary>
        /// A cache lookup.
        /// </summary>
        public const string Get = "cache.get";

        /// <summary>
        /// An invalidation by key or tag.
        /// </summary>
        public const string Delete = "cache.delete";
    }

    /// <summary>
    /// Attribute names on MethodCache spans.
    /// </summary>
    public static class AttributeNames
    {
        /// <summary>
        /// <c>true</c> for a cache hit, <c>false</c> for a miss.
        /// </summary>
        public const string CacheHit = "cache.hit";

        /// <summary>
        /// The cached method's name.
        /// </summary>
        public const string CacheMethod = "cache.method";

        /// <summary>
        /// The cache key, only when <c>RecordCacheKeys</c> is on and <c>HashCacheKeys</c> is off.
        /// </summary>
        public const string CacheKey = "cache.key";

        /// <summary>
        /// A hash of the cache key, when <c>RecordCacheKeys</c> and <c>HashCacheKeys</c> are on.
        /// </summary>
        public const string CacheKeyHash = "cache.key_hash";

        /// <summary>
        /// The entry's time to live in seconds.
        /// </summary>
        public const string CacheTtl = "cache.ttl_seconds";

        /// <summary>
        /// The entry's tags, or the invalidated tags, only when <c>RecordCacheTags</c> is on.
        /// </summary>
        public const string CacheTags = "cache.tags";

        /// <summary>
        /// The method's group.
        /// </summary>
        public const string CacheGroup = "cache.group";

        /// <summary>
        /// The method's version.
        /// </summary>
        public const string CacheVersion = "cache.version";

        /// <summary>
        /// <c>true</c> when the operation failed.
        /// </summary>
        public const string CacheError = "cache.error";

        /// <summary>
        /// The type of the exception when the operation failed.
        /// </summary>
        public const string CacheErrorType = "cache.error_type";
    }

    /// <summary>
    /// Baggage keys set by <see cref="IBaggagePropagator"/>.
    /// </summary>
    public static class BaggageKeys
    {
        /// <summary>
        /// The cache correlation id.
        /// </summary>
        public const string CacheCorrelationId = "cache.correlation_id";

        /// <summary>
        /// The id of the invalidation in progress.
        /// </summary>
        public const string CacheInvalidationId = "cache.invalidation_id";

        /// <summary>
        /// The user id, only when <c>ExportSensitiveData</c> is on.
        /// </summary>
        public const string CacheUserId = "cache.user_id";

        /// <summary>
        /// The tenant id, only when <c>ExportSensitiveData</c> is on.
        /// </summary>
        public const string CacheTenantId = "cache.tenant_id";
    }

    /// <summary>
    /// The activity source MethodCache creates its spans from.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, ActivitySourceVersion);
}
