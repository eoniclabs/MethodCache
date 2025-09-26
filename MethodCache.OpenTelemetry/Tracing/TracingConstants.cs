using System.Diagnostics;

namespace MethodCache.OpenTelemetry.Tracing;

public static class TracingConstants
{
    public const string ActivitySourceName = "MethodCache";
    public const string ActivitySourceVersion = "1.0.0";

    public static class Operations
    {
        public const string Get = "cache.get";
        public const string Set = "cache.set";
        public const string Delete = "cache.delete";
        public const string Clear = "cache.clear";
        public const string KeyGeneration = "cache.key_generation";
        public const string Serialization = "cache.serialization";
        public const string Deserialization = "cache.deserialization";
        public const string StorageOperation = "cache.storage_operation";
    }

    public static class AttributeNames
    {
        public const string CacheHit = "cache.hit";
        public const string CacheMethod = "cache.method";
        public const string CacheKey = "cache.key";
        public const string CacheKeyHash = "cache.key_hash";
        public const string CacheTtl = "cache.ttl_seconds";
        public const string CacheProvider = "cache.provider";
        public const string CacheTags = "cache.tags";
        public const string CacheGroup = "cache.group";
        public const string CacheVersion = "cache.version";
        public const string CacheSize = "cache.size_bytes";
        public const string CacheEvicted = "cache.evicted";
        public const string CacheEvictionReason = "cache.eviction_reason";
        public const string CacheError = "cache.error";
        public const string CacheErrorType = "cache.error_type";
        public const string CacheCompressed = "cache.compressed";
        public const string CacheStampedeProtection = "cache.stampede_protection";
        public const string CacheRegion = "cache.region";
        public const string CacheNamespace = "cache.namespace";

        public const string HttpRequestId = "http.request_id";
        public const string HttpMethod = "http.method";
        public const string HttpPath = "http.path";
        public const string HttpStatusCode = "http.status_code";
    }

    public static class BaggageKeys
    {
        public const string CacheCorrelationId = "cache.correlation_id";
        public const string CacheInvalidationId = "cache.invalidation_id";
        public const string CacheUserId = "cache.user_id";
        public const string CacheTenantId = "cache.tenant_id";
    }

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, ActivitySourceVersion);
}