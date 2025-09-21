using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;

namespace MethodCache.HttpCaching;

/// <summary>
/// HTTP message handler that implements standards-compliant HTTP caching according to RFC 7234.
/// </summary>
public class HttpCacheHandler : DelegatingHandler
{
    private readonly IHttpCacheStorage _storage;
    private readonly HttpCacheOptions _options;
    private readonly FreshnessCalculator _freshnessCalculator;
    private readonly VaryHeaderCacheKeyGenerator _varyKeyGenerator;
    private readonly ILogger<HttpCacheHandler> _logger;

    public HttpCacheHandler(
        IHttpCacheStorage storage,
        HttpCacheOptions options,
        ILogger<HttpCacheHandler> logger)
    {
        _storage = storage;
        _options = options;
        _freshnessCalculator = new FreshnessCalculator(options);
        _varyKeyGenerator = new VaryHeaderCacheKeyGenerator();
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only cache safe methods that are configured as cacheable
        if (!IsMethodCacheable(request.Method))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Check if request explicitly bypasses cache
        if (ShouldBypassCache(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var cacheKey = GenerateCacheKey(request);
        var cachedEntry = await GetCacheEntryAsync(request, cacheKey, cancellationToken);

        _logger.LogDebug("Cache lookup for {Method} {Uri}: {Result}",
            request.Method, request.RequestUri, cachedEntry != null ? "HIT" : "MISS");

        // Check if we can serve from cache (fresh)
        if (cachedEntry != null && _freshnessCalculator.IsFresh(cachedEntry))
        {
            _logger.LogDebug("Serving fresh cached response for {Uri}", request.RequestUri);
            return CreateCacheHitResponse(cachedEntry, "FRESH");
        }

        // Check if we can use stale content while revalidating
        if (cachedEntry != null && _freshnessCalculator.CanUseStaleWhileRevalidate(cachedEntry))
        {
            _logger.LogDebug("Using stale-while-revalidate for {Uri}", request.RequestUri);

            // Start background revalidation (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RevalidateCacheEntry(request, cachedEntry, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background revalidation failed for {Uri}", request.RequestUri);
                }
            }, cancellationToken);

            return CreateCacheHitResponse(cachedEntry, "STALE-WHILE-REVALIDATE");
        }

        // Add conditional headers for validation if we have stale cached content
        if (cachedEntry != null && cachedEntry.CanValidate())
        {
            AddConditionalHeaders(request, cachedEntry);
        }

        HttpResponseMessage response;
        try
        {
            // Execute the request
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (cachedEntry != null && _freshnessCalculator.CanUseStaleIfError(cachedEntry))
        {
            _logger.LogWarning(ex, "Request failed, serving stale content for {Uri}", request.RequestUri);
            return CreateCacheHitResponse(cachedEntry, "STALE-IF-ERROR");
        }

        // Handle 304 Not Modified
        if (response.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
        {
            _logger.LogDebug("Received 304 Not Modified for {Uri}, updating cache", request.RequestUri);

            // Update cached entry with new headers
            var updatedEntry = cachedEntry.WithUpdatedHeaders(response);
            await StoreCacheEntryAsync(request, cacheKey, updatedEntry, cancellationToken);

            response.Dispose(); // Dispose the 304 response
            return CreateCacheHitResponse(updatedEntry, "REVALIDATED");
        }

        // Cache successful responses according to HTTP rules
        if (ShouldCacheResponse(request, response))
        {
            try
            {
                var cacheEntry = await HttpCacheEntry.FromResponseAsync(request, response);
                await StoreCacheEntryAsync(request, cacheKey, cacheEntry, cancellationToken);

                _logger.LogDebug("Cached response for {Uri}, size: {Size} bytes",
                    request.RequestUri, cacheEntry.Content.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache response for {Uri}", request.RequestUri);
            }
        }

        // Add diagnostic headers if enabled
        if (_options.AddDiagnosticHeaders)
        {
            response.Headers.TryAddWithoutValidation("X-Cache", "MISS");
        }

        return response;
    }

    private bool IsMethodCacheable(HttpMethod method)
    {
        return _options.CacheableMethods.Contains(method);
    }

    private bool ShouldBypassCache(HttpRequestMessage request)
    {
        var cacheControl = request.Headers.CacheControl;
        if (cacheControl == null) return false;

        // Check for no-cache or no-store directives
        return cacheControl.NoCache || cacheControl.NoStore;
    }

    private bool ShouldCacheResponse(HttpRequestMessage request, HttpResponseMessage response)
    {
        // Don't cache if response is too large
        if (response.Content.Headers.ContentLength > _options.MaxResponseSize)
        {
            return false;
        }

        // Don't cache errors unless explicitly cacheable
        if (!response.IsSuccessStatusCode &&
            response.StatusCode != HttpStatusCode.NotModified &&
            response.StatusCode != HttpStatusCode.MovedPermanently &&
            response.StatusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        var cacheControl = response.Headers.CacheControl;

        // Explicit no-store directive
        if (cacheControl?.NoStore == true)
        {
            return false;
        }

        // Private responses in shared cache
        if (cacheControl?.Private == true && _options.IsSharedCache)
        {
            return false;
        }

        // Check request's Cache-Control
        var requestCacheControl = request.Headers.CacheControl;
        if (requestCacheControl?.NoStore == true)
        {
            return false;
        }

        // Must have some way to determine freshness or validation
        return HasFreshnessInfo(response) || HasValidationInfo(response);
    }

    private bool HasFreshnessInfo(HttpResponseMessage response)
    {
        return response.Headers.CacheControl?.MaxAge != null ||
               response.Content?.Headers?.Expires != null ||
               (response.Content?.Headers?.LastModified != null && _options.AllowHeuristicFreshness);
    }

    private bool HasValidationInfo(HttpResponseMessage response)
    {
        return response.Headers.ETag != null ||
               response.Content?.Headers?.LastModified != null;
    }

    private void AddConditionalHeaders(HttpRequestMessage request, HttpCacheEntry cachedEntry)
    {
        // Add If-None-Match for ETag validation
        if (!string.IsNullOrEmpty(cachedEntry.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cachedEntry.ETag));
        }

        // Add If-Modified-Since for Last-Modified validation
        if (cachedEntry.LastModified.HasValue)
        {
            request.Headers.IfModifiedSince = cachedEntry.LastModified.Value;
        }
    }

    private HttpResponseMessage CreateCacheHitResponse(HttpCacheEntry entry, string cacheStatus)
    {
        var response = entry.ToHttpResponse();

        // Add diagnostic headers if enabled
        if (_options.AddDiagnosticHeaders)
        {
            response.Headers.TryAddWithoutValidation("X-Cache", cacheStatus);
            response.Headers.TryAddWithoutValidation("X-Cache-Age", entry.GetAge().TotalSeconds.ToString("0"));

            var freshnessLifetime = CalculateFreshnessLifetime(entry);
            if (freshnessLifetime.HasValue)
            {
                var ttl = freshnessLifetime.Value - entry.GetAge();
                response.Headers.TryAddWithoutValidation("X-Cache-TTL", Math.Max(0, ttl.TotalSeconds).ToString("0"));
            }
        }

        return response;
    }

    private TimeSpan? CalculateFreshnessLifetime(HttpCacheEntry entry)
    {
        if (entry.CacheControl?.MaxAge.HasValue == true)
        {
            return entry.CacheControl.MaxAge.Value;
        }

        if (entry.Expires.HasValue && entry.Date.HasValue)
        {
            return entry.Expires.Value - entry.Date.Value;
        }

        return null;
    }

    private string GenerateCacheKey(HttpRequestMessage request)
    {
        // Simple cache key generation - could be enhanced with Vary header support
        var uri = request.RequestUri?.ToString() ?? "";
        var method = request.Method.ToString();

        // Include important headers that affect caching
        var keyBuilder = new System.Text.StringBuilder();
        keyBuilder.Append(method);
        keyBuilder.Append(':');
        keyBuilder.Append(uri);

        // Include Authorization header hash if present (for private caching)
        if (request.Headers.Authorization != null)
        {
            keyBuilder.Append(':');
            keyBuilder.Append(request.Headers.Authorization.GetHashCode());
        }

        return keyBuilder.ToString();
    }

    /// <summary>
    /// Gets a cache entry with Vary header support.
    /// </summary>
    private async Task<HttpCacheEntry?> GetCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        CancellationToken cancellationToken)
    {
        // First, try to get the entry with base key
        var entry = await _storage.GetAsync(baseKey, cancellationToken);

        // If no entry found, return null
        if (entry == null)
        {
            return null;
        }

        // If entry has no Vary headers, return it directly
        if (entry.VaryHeaders == null || entry.VaryHeaders.Length == 0)
        {
            return entry;
        }

        // Entry has Vary headers, so we need to check if it matches current request
        if (!_options.RespectVary)
        {
            return entry; // Ignore Vary if configured to do so
        }

        // Generate Vary-aware cache key and check if we have a specific entry
        var varyKey = _varyKeyGenerator.GenerateKey(request, entry.VaryHeaders);

        // If Vary key indicates uncacheable, return null
        if (varyKey.StartsWith("UNCACHEABLE"))
        {
            return null;
        }

        // Try to get the Vary-specific entry
        var varyEntry = await _storage.GetAsync(varyKey, cancellationToken);
        return varyEntry;
    }

    /// <summary>
    /// Stores a cache entry with Vary header support.
    /// </summary>
    private async Task StoreCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        HttpCacheEntry entry,
        CancellationToken cancellationToken)
    {
        // If no Vary headers, store with base key only
        if (entry.VaryHeaders == null || entry.VaryHeaders.Length == 0)
        {
            await _storage.SetAsync(baseKey, entry, cancellationToken);
            return;
        }

        // Response has Vary headers
        if (!_options.RespectVary)
        {
            // If we're ignoring Vary, just store with base key
            await _storage.SetAsync(baseKey, entry, cancellationToken);
            return;
        }

        // Generate Vary-specific key
        var varyKey = _varyKeyGenerator.GenerateKey(request, entry.VaryHeaders);

        // Don't cache if Vary indicates uncacheable
        if (varyKey.StartsWith("UNCACHEABLE"))
        {
            return;
        }

        // Store with both keys:
        // 1. Base key for Vary header discovery
        // 2. Vary-specific key for actual content retrieval
        await _storage.SetAsync(baseKey, entry, cancellationToken);
        await _storage.SetAsync(varyKey, entry, cancellationToken);
    }

    private async Task RevalidateCacheEntry(
        HttpRequestMessage originalRequest,
        HttpCacheEntry cachedEntry,
        CancellationToken cancellationToken)
    {
        // Create a new request for revalidation
        var revalidationRequest = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri);

        // Copy relevant headers
        foreach (var header in originalRequest.Headers)
        {
            revalidationRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Add conditional headers
        AddConditionalHeaders(revalidationRequest, cachedEntry);

        try
        {
            var response = await base.SendAsync(revalidationRequest, cancellationToken);

            var cacheKey = GenerateCacheKey(originalRequest);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                // Update existing cache entry
                var updatedEntry = cachedEntry.WithUpdatedHeaders(response);
                await _storage.SetAsync(cacheKey, updatedEntry, cancellationToken);
            }
            else if (ShouldCacheResponse(originalRequest, response))
            {
                // Store new response
                var newEntry = await HttpCacheEntry.FromResponseAsync(originalRequest, response);
                await _storage.SetAsync(cacheKey, newEntry, cancellationToken);
            }

            response.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background revalidation failed");
        }
        finally
        {
            revalidationRequest.Dispose();
        }
    }
}