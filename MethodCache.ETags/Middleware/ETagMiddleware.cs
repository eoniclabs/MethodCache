using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Models;
using MethodCache.ETags.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace MethodCache.ETags.Middleware
{
    /// <summary>
    /// HTTP middleware that provides automatic ETag generation and validation for responses.
    /// Integrates with the ETag-aware hybrid cache for optimal performance.
    /// </summary>
    public class ETagMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IETagCacheManager _etagCache;
        private readonly ILogger<ETagMiddleware> _logger;
        private readonly ETagMiddlewareOptions _options;

        public ETagMiddleware(
            RequestDelegate next,
            IETagCacheManager etagCache,
            IOptions<ETagMiddlewareOptions> options,
            ILogger<ETagMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _etagCache = etagCache ?? throw new ArgumentNullException(nameof(etagCache));
            _options = options.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Skip ETag processing for non-eligible requests
            if (!IsETagEligible(context))
            {
                await _next(context);
                return;
            }

            var cacheKey = GenerateCacheKey(context.Request);
            var ifNoneMatch = GetIfNoneMatchHeader(context.Request);
            var mustRevalidate = ShouldRevalidateOnly(context.Request);

            _logger.LogDebug("Processing ETag request for key {CacheKey} with If-None-Match: {IfNoneMatch}, MustRevalidate: {MustRevalidate}", 
                cacheKey, ifNoneMatch ?? "null", mustRevalidate);

            try
            {
                ETagCacheResult<ResponseCacheEntry> result;
                
                if (mustRevalidate)
                {
                    // For no-cache/must-revalidate: always execute factory but still check ETag
                    result = await _etagCache.GetOrCreateWithETagAsync<ResponseCacheEntry>(
                        cacheKey,
                        async () => await CaptureResponseAsync(context),
                        ifNoneMatch,
                        _options.GetCacheSettings(),
                        forceRefresh: true);
                }
                else
                {
                    // Normal caching behavior
                    result = await _etagCache.GetOrCreateWithETagAsync<ResponseCacheEntry>(
                        cacheKey,
                        async () => await CaptureResponseAsync(context),
                        ifNoneMatch,
                        _options.GetCacheSettings());
                }

                // Handle bypass results early
                if (result.ShouldBypass)
                {
                    return;
                }

                await HandleETagResult(context, result, ifNoneMatch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing ETag middleware for key {CacheKey}", cacheKey);
                // Continue with normal processing on error
                await _next(context);
            }
        }

        private bool IsETagEligible(HttpContext context)
        {
            var request = context.Request;

            // Only process GET and HEAD requests
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
                return false;

            // Skip if explicitly disabled
            if (_options.SkipPaths?.Any(path => request.Path.StartsWithSegments(path)) == true)
                return false;

            // Cache-Control: no-cache allows revalidation but not serving from cache without validation
            // We'll handle this in the cache logic, not skip ETag processing entirely
            
            return true;
        }

        private bool ShouldRevalidateOnly(HttpRequest request)
        {
            var cacheControl = request.Headers["Cache-Control"].ToString();
            return cacheControl.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ||
                   cacheControl.Contains("must-revalidate", StringComparison.OrdinalIgnoreCase);
        }

        private string GenerateCacheKey(HttpRequest request)
        {
            var keyBuilder = new StringBuilder();
            keyBuilder.Append(request.Method);
            keyBuilder.Append(':');
            keyBuilder.Append(request.Path);

            // Include query string if specified
            if (_options.IncludeQueryStringInKey && request.QueryString.HasValue)
            {
                keyBuilder.Append(request.QueryString.Value);
            }

            // Include custom headers if specified
            if (_options.HeadersToIncludeInKey?.Length > 0)
            {
                foreach (var header in _options.HeadersToIncludeInKey)
                {
                    if (request.Headers.TryGetValue(header, out var values))
                    {
                        keyBuilder.Append($"|{header}:{string.Join(",", values.ToArray())}");
                    }
                }
            }

            // Include Accept header for content negotiation
            if (request.Headers.TryGetValue("Accept", out var acceptValues))
            {
                keyBuilder.Append($"|Accept:{string.Join(",", acceptValues.ToArray())}");
            }

            // Include Accept-Encoding for compression negotiation
            if (request.Headers.TryGetValue("Accept-Encoding", out var encodingValues))
            {
                keyBuilder.Append($"|Accept-Encoding:{string.Join(",", encodingValues.ToArray())}");
            }

            // Add personalization context (user/tenant)
            var personalization = GetPersonalizationContext(request);
            if (!string.IsNullOrEmpty(personalization))
            {
                keyBuilder.Append($"|User:{personalization}");
            }

            // Hash the key if it's too long
            var key = keyBuilder.ToString();
            if (key.Length > 250)
            {
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                key = ToBase64UrlString(hash);
            }

            return $"etag:{key}";
        }

        private static string? GetIfNoneMatchHeader(HttpRequest request)
        {
            var ifNoneMatch = request.Headers["If-None-Match"].ToString();
            return string.IsNullOrEmpty(ifNoneMatch) ? null : ifNoneMatch.Trim();
        }

        private static bool MatchesIfNoneMatch(string currentETag, string? ifNoneMatch)
        {
            if (string.IsNullOrEmpty(ifNoneMatch) || string.IsNullOrEmpty(currentETag))
                return false;

            // Handle "*" wildcard
            if (ifNoneMatch.Trim() == "*")
                return true;

            // Parse multiple ETags and check for matches
            var clientETags = ETagUtilities.ParseIfNoneMatch(ifNoneMatch);
            var formattedCurrentETag = FormatETagHeader(currentETag);
            
            return clientETags.Any(clientETag => 
                ETagUtilities.ETagsMatch(formattedCurrentETag, clientETag));
        }

        private async Task<ETagCacheEntry<ResponseCacheEntry>> CaptureResponseAsync(HttpContext context)
        {
            // Capture the original response stream
            var originalBodyStream = context.Response.Body;

            using var responseBodyStream = new MemoryStream();
            context.Response.Body = responseBodyStream;

            // Execute the rest of the pipeline
            await _next(context);

            // Only cache successful 2xx responses
            if (context.Response.StatusCode < 200 || context.Response.StatusCode >= 300)
            {
                // Don't cache error responses, just pass through
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                await responseBodyStream.CopyToAsync(originalBodyStream);
                context.Response.Body = originalBodyStream;
                return ETagCacheEntry<ResponseCacheEntry>.Bypass();
            }

            // Check for streaming or large content that shouldn't be cached
            var contentType = context.Response.ContentType;
            if (IsStreamingContent(contentType) || responseBodyStream.Length > _options.MaxResponseBodySize)
            {
                // Don't cache streaming content or large responses
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                await responseBodyStream.CopyToAsync(originalBodyStream);
                context.Response.Body = originalBodyStream;
                return ETagCacheEntry<ResponseCacheEntry>.Bypass();
            }

            // Check content type eligibility
            if (_options.CacheableContentTypes?.Length > 0)
            {
                if (contentType == null || !_options.CacheableContentTypes.Any(ct => contentType.StartsWith(ct)))
                {
                    // Not a cacheable content type, so just write the response back and return null
                    responseBodyStream.Seek(0, SeekOrigin.Begin);
                    await responseBodyStream.CopyToAsync(originalBodyStream);
                    context.Response.Body = originalBodyStream;
                    return ETagCacheEntry<ResponseCacheEntry>.Bypass();
                }
            }

            // Don't cache empty responses (204 No Content, etc.)
            if (responseBodyStream.Length == 0 && context.Response.StatusCode == 204)
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                await responseBodyStream.CopyToAsync(originalBodyStream);
                context.Response.Body = originalBodyStream;
                return ETagCacheEntry<ResponseCacheEntry>.Bypass();
            }

            // Read the response
            responseBodyStream.Seek(0, SeekOrigin.Begin);
            var responseBody = responseBodyStream.ToArray();

            // Generate ETag
            var etag = GenerateETag(responseBody, context.Response.Headers);

            // Capture response metadata
            var lastModified = GetLastModifiedFromResponse(context.Response);
            var cacheEntry = new ResponseCacheEntry
            {
                Body = responseBody,
                StatusCode = context.Response.StatusCode,
                ContentType = context.Response.ContentType,
                LastModified = lastModified,
                Headers = CaptureHeaders(context.Response)
            };

            // Restore original stream and write response
            context.Response.Body = originalBodyStream;

            // Add Vary header based on headers that influence caching
            AddVaryHeaders(context.Response);

            return ETagCacheEntry<ResponseCacheEntry>.WithValue(cacheEntry, etag);
        }

        private string GenerateETag(byte[] content, IHeaderDictionary headers)
        {
            using var sha256 = SHA256.Create();
            
            // Hash content
            var contentHash = sha256.ComputeHash(content);
            
            // Include relevant headers in hash if specified
            if (_options.HeadersToIncludeInETag?.Length > 0)
            {
                var headerData = new StringBuilder();
                foreach (var headerName in _options.HeadersToIncludeInETag)
                {
                    if (headers.TryGetValue(headerName, out var values))
                    {
                        headerData.Append($"{headerName}:{string.Join(",", values.ToArray())}");
                    }
                }
                
                if (headerData.Length > 0)
                {
                    var headerBytes = Encoding.UTF8.GetBytes(headerData.ToString());
                    var combinedHash = contentHash.Concat(headerBytes).ToArray();
                    contentHash = sha256.ComputeHash(combinedHash);
                }
            }

            // Return raw ETag value without quotes
            return Convert.ToBase64String(contentHash);
        }

        private Dictionary<string, string> CaptureHeaders(HttpResponse response)
        {
            var headers = new Dictionary<string, string>();

            // Capture headers specified in options
            if (_options.HeadersToCache?.Length > 0)
            {
                foreach (var headerName in _options.HeadersToCache)
                {
                    if (response.Headers.TryGetValue(headerName, out var values))
                    {
                        headers[headerName] = string.Join(", ", values.ToArray());
                    }
                }
            }

            return headers;
        }

        private async Task HandleETagResult(HttpContext context, ETagCacheResult<ResponseCacheEntry> result, string? ifNoneMatch)
        {
            if (result.ShouldBypass)
            {
                // Bypass caching - response was already written during CaptureResponseAsync
                return;
            }

            // Set ETag header with proper quoting
            var formattedETag = FormatETagHeader(result.ETag);
            context.Response.Headers["ETag"] = formattedETag;

            // Check for 304 Not Modified using proper If-None-Match handling
            if (MatchesIfNoneMatch(result.ETag, ifNoneMatch))
            {
                // Return 304 Not Modified
                context.Response.StatusCode = 304;
                _logger.LogDebug("Returning 304 Not Modified for ETag {ETag}", formattedETag);
                return;
            }

            // Serve from cache or fresh response
            var entry = result.Value!;

            // Set status code and content type
            context.Response.StatusCode = entry.StatusCode;
            if (!string.IsNullOrEmpty(entry.ContentType))
            {
                context.Response.ContentType = entry.ContentType;
            }

            // Restore cached headers
            foreach (var header in entry.Headers)
            {
                if (!context.Response.Headers.ContainsKey(header.Key))
                {
                    context.Response.Headers[header.Key] = header.Value;
                }
            }

            // Set cache headers
            if (_options.AddCacheControlHeader)
            {
                var maxAge = _options.DefaultCacheMaxAge?.TotalSeconds ?? 3600;
                context.Response.Headers["Cache-Control"] = $"public, max-age={maxAge}";
            }

            if (_options.AddLastModifiedHeader)
            {
                var lastModified = entry.LastModified ?? result.LastModified;
                context.Response.Headers["Last-Modified"] = lastModified.ToString("R");
            }

            // Write response body (skip for HEAD requests)
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(entry.Body);
            }

            var statusText = result.Status == ETagCacheStatus.Hit ? "cache hit" : "cache miss";
            _logger.LogDebug("Served response with ETag {ETag} ({Status})", formattedETag, statusText);
        }

        private bool IsStreamingContent(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType) || _options.StreamingContentTypes?.Length == 0)
                return false;

            return _options.StreamingContentTypes?.Any(streamingType =>
                contentType.StartsWith(streamingType, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private string? GetPersonalizationContext(HttpRequest request)
        {
            // Use custom personalizer if provided
            if (_options.KeyPersonalizer != null)
            {
                try
                {
                    return _options.KeyPersonalizer(request.HttpContext);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in key personalizer function");
                }
            }

            // Use personalization headers if configured
            if (_options.PersonalizationHeaders?.Length > 0)
            {
                var personalizationParts = new List<string>();
                foreach (var header in _options.PersonalizationHeaders)
                {
                    if (request.Headers.TryGetValue(header, out var values))
                    {
                        personalizationParts.Add($"{header}:{string.Join(",", values.ToArray())}");
                    }
                }
                
                if (personalizationParts.Count > 0)
                {
                    return string.Join("|", personalizationParts);
                }
            }

            return null;
        }

        private static DateTime? GetLastModifiedFromResponse(HttpResponse response)
        {
            if (response.Headers.TryGetValue("Last-Modified", out var lastModifiedValues))
            {
                var lastModifiedString = lastModifiedValues.FirstOrDefault();
                if (DateTime.TryParse(lastModifiedString, out var lastModified))
                {
                    return lastModified;
                }
            }
            return DateTime.UtcNow;
        }

        private void AddVaryHeaders(HttpResponse response)
        {
            var varyHeaders = new List<string>();
            
            // Preserve existing Vary headers
            if (response.Headers.TryGetValue("Vary", out var existingVary))
            {
                var existing = existingVary.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => h.Trim());
                varyHeaders.AddRange(existing);
            }
            
            // Add our standard headers
            varyHeaders.Add("Accept");

            // Add headers that are included in cache key generation
            if (_options.HeadersToIncludeInKey?.Length > 0)
            {
                varyHeaders.AddRange(_options.HeadersToIncludeInKey);
            }

            // Add headers that influence ETag generation
            if (_options.HeadersToIncludeInETag?.Length > 0)
            {
                varyHeaders.AddRange(_options.HeadersToIncludeInETag);
            }

            // Always include Accept-Encoding for compression
            if (!varyHeaders.Contains("Accept-Encoding"))
            {
                varyHeaders.Add("Accept-Encoding");
            }

            response.Headers["Vary"] = string.Join(", ", varyHeaders.Distinct());
        }

        private static string ToBase64UrlString(byte[] input)
        {
            var base64 = Convert.ToBase64String(input);
            return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string FormatETagHeader(string etag)
        {
            if (string.IsNullOrEmpty(etag))
                return string.Empty;

            // If it's already quoted, return as-is
            if ((etag.StartsWith("\"") && etag.EndsWith("\"")) ||
                (etag.StartsWith("W/\"") && etag.EndsWith("\"")))
            {
                return etag;
            }

            // If it's a weak ETag marker without quotes
            if (etag.StartsWith("W/"))
            {
                var value = etag.Substring(2);
                return $"W/\"{value}\"";
            }

            // Strong ETag - add quotes
            return $"\"{etag}\"";
        }
    }
}