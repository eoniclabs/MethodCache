using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime;
using Microsoft.AspNetCore.Http;

namespace MethodCache.ETags.Middleware
{
    /// <summary>
    /// Configuration options for the ETag middleware.
    /// </summary>
    public class ETagMiddlewareOptions
    {
        /// <summary>
        /// Whether to include query string parameters in the cache key.
        /// Default: true
        /// </summary>
        public bool IncludeQueryStringInKey { get; set; } = true;

        /// <summary>
        /// Headers to include when generating the cache key.
        /// This makes caching aware of these headers.
        /// </summary>
        public string[]? HeadersToIncludeInKey { get; set; }

        /// <summary>
        /// Headers to include when generating the ETag.
        /// Changes to these headers will result in different ETags.
        /// </summary>
        public string[]? HeadersToIncludeInETag { get; set; }

        /// <summary>
        /// Headers to cache along with the response body.
        /// These headers will be restored when serving from cache.
        /// </summary>
        public string[]? HeadersToCache { get; set; }

        /// <summary>
        /// Content types that are eligible for ETag caching.
        /// If null or empty, all content types are eligible.
        /// </summary>
        public string[]? CacheableContentTypes { get; set; }

        /// <summary>
        /// Paths to skip ETag processing.
        /// Useful for API endpoints or dynamic content.
        /// </summary>
        public string[]? SkipPaths { get; set; }

        /// <summary>
        /// Whether to add Cache-Control headers to responses.
        /// Default: true
        /// </summary>
        public bool AddCacheControlHeader { get; set; } = true;

        /// <summary>
        /// Whether to add Last-Modified headers to responses.
        /// Default: true
        /// </summary>
        public bool AddLastModifiedHeader { get; set; } = true;

        /// <summary>
        /// Default cache max-age for Cache-Control header.
        /// Default: 1 hour
        /// </summary>
        public TimeSpan? DefaultCacheMaxAge { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Default expiration time for cached responses.
        /// Default: 24 hours
        /// </summary>
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Tags to apply to all ETag cache entries.
        /// Useful for bulk invalidation.
        /// </summary>
        public string[]? DefaultTags { get; set; }

        /// <summary>
        /// Maximum response body size to buffer for caching (in bytes).
        /// Responses larger than this will not be cached.
        /// Default: 10MB
        /// </summary>
        public long MaxResponseBodySize { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// Content types that should never be cached due to their streaming nature.
        /// </summary>
        public string[]? StreamingContentTypes { get; set; } = new[] 
        {
            "text/event-stream",
            "application/octet-stream",
            "video/",
            "audio/"
        };

        /// <summary>
        /// Function to extract user/tenant context for cache key personalization.
        /// Takes HttpContext and returns a string to include in cache keys.
        /// </summary>
        public Func<HttpContext, string?>? KeyPersonalizer { get; set; }

        /// <summary>
        /// Headers to use for user/tenant identification in cache keys.
        /// Common examples: "Authorization", "X-Tenant-Id", "X-User-Id"
        /// </summary>
        public string[]? PersonalizationHeaders { get; set; }

        /// <summary>
        /// Gets the runtime descriptor for storing ETag entries.
        /// </summary>
        internal CacheRuntimeDescriptor GetRuntimeDescriptor()
        {
            var policy = CachePolicy.Empty with
            {
                Duration = DefaultExpiration,
                Tags = DefaultTags ?? Array.Empty<string>()
            };

            return CacheRuntimeDescriptor.FromPolicy(
                "ETagMiddleware",
                policy,
                CachePolicyFields.Duration | CachePolicyFields.Tags);
        }
    }

    /// <summary>
    /// Represents a cached HTTP response entry.
    /// </summary>
    public class ResponseCacheEntry
    {
        /// <summary>
        /// The response body content.
        /// </summary>
        public byte[] Body { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// The HTTP status code.
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// The response content type.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// The original Last-Modified timestamp.
        /// </summary>
        public DateTime? LastModified { get; set; }

        /// <summary>
        /// Additional headers to restore with the response.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new();
    }
}