using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Threading;
using MethodCache.HttpCaching.Metrics;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching;

/// <summary>
/// HTTP message handler that implements standards-compliant HTTP caching according to RFC 9111.
/// </summary>
public class HttpCacheHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> VariantIndex = new(StringComparer.Ordinal);

    private readonly IHttpCacheStorage _storage;
    private readonly IOptionsMonitor<HttpCacheOptions> _optionsMonitor;
    private readonly VaryHeaderCacheKeyGenerator _varyKeyGenerator;
    private readonly IHttpCacheMetrics? _metrics;
    private readonly ILogger<HttpCacheHandler> _logger;

    public HttpCacheHandler(
        IHttpCacheStorage storage,
        IOptionsMonitor<HttpCacheOptions> optionsMonitor,
        VaryHeaderCacheKeyGenerator varyKeyGenerator,
        ILogger<HttpCacheHandler> logger,
        IHttpCacheMetrics? metrics = null)
    {
        _storage = storage;
        _optionsMonitor = optionsMonitor;
        _varyKeyGenerator = varyKeyGenerator;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var behavior = options.Behavior;
        var freshnessOptions = options.Freshness;
        var variation = options.Variation;
        var diagnostics = options.Diagnostics;
        var metricsEnabled = options.Metrics.EnableMetrics && _metrics != null;

        var stopwatch = metricsEnabled ? Stopwatch.StartNew() : null;

        var requestDirectives = RequestCacheDirectives.Parse(request);

        if (!IsMethodCacheable(request.Method, options) || ShouldBypassCache(behavior, requestDirectives))
        {
            var bypassResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            RecordBypassMetrics(request, bypassResponse, stopwatch, metricsEnabled);
            return bypassResponse;
        }

        var negotiationHandler = new ContentNegotiationHandler(variation);
        var acceptedContent = negotiationHandler.ParseAcceptHeader(request);
        var freshnessCalculator = new FreshnessCalculator(behavior, freshnessOptions);
        var advancedDirectives = new AdvancedCacheDirectives(behavior);

        var cacheKey = GenerateCacheKey(request, options, acceptedContent);
        var cachedEntry = await GetCacheEntryAsync(request, cacheKey, behavior, variation, negotiationHandler, acceptedContent, cancellationToken).ConfigureAwait(false);

        if (cachedEntry == null && requestDirectives.RequiresCacheOnly(behavior))
        {
            stopwatch?.Stop();
            var timeout = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = request,
                ReasonPhrase = "Cache entry not available"
            };

            if (behavior.EnableWarningHeaders)
            {
                timeout.Headers.TryAddWithoutValidation("Warning", "112 - \"Cache miss\"");
            }

            RecordBypassMetrics(request, timeout, stopwatch, metricsEnabled);
            return timeout;
        }

        if (cachedEntry != null)
        {
            var freshnessLifetime = freshnessCalculator.GetFreshnessLifetime(cachedEntry);
            var currentAge = freshnessCalculator.GetCurrentAge(cachedEntry);

            if (freshnessLifetime.HasValue &&
                requestDirectives.IsSatisfiedBy(cachedEntry, currentAge, freshnessLifetime.Value, behavior) &&
                freshnessCalculator.IsFresh(cachedEntry))
            {
                var hit = CreateCacheHitResponse(cachedEntry, "HIT", options, freshnessLifetime);
                RecordHitMetrics(request, hit, stopwatch, metricsEnabled);
                return hit;
            }

            if (freshnessLifetime.HasValue && currentAge > freshnessLifetime.Value && freshnessCalculator.CanUseStaleWhileRevalidate(cachedEntry))
            {
                var stale = CreateCacheHitResponse(cachedEntry, "STALE", options, freshnessLifetime);
                RecordStaleMetrics(request, stale, stopwatch, metricsEnabled);

                _ = QueueRevalidationAsync(request, cachedEntry, cacheKey, options);
                return stale;
            }
        }

        // Add conditional headers for validation if we have a stale cached entry
        if (cachedEntry != null)
        {
            AddConditionalHeaders(request, cachedEntry);
        }

        HttpResponseMessage upstreamResponse;

        try
        {
            upstreamResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (cachedEntry != null && freshnessCalculator.CanUseStaleIfError(cachedEntry))
        {
            _logger.LogWarning(ex, "Request failed, serving stale content for {Uri}", request.RequestUri);
            var staleError = CreateCacheHitResponse(cachedEntry, "STALE-IF-ERROR", options, freshnessCalculator.GetFreshnessLifetime(cachedEntry));
            RecordStaleMetrics(request, staleError, stopwatch, metricsEnabled);
            return staleError;
        }

        var responseDirectives = advancedDirectives.ParseResponse(upstreamResponse);

        if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
        {
            _logger.LogDebug("Received 304 Not Modified for {Uri}, updating cache", request.RequestUri);
            var updatedEntry = cachedEntry.WithUpdatedHeaders(upstreamResponse);
            await StoreCacheEntryAsync(request, cacheKey, updatedEntry, options, variation, cancellationToken).ConfigureAwait(false);

            upstreamResponse.Dispose();
            var revalidated = CreateCacheHitResponse(updatedEntry, "REVALIDATED", options, freshnessCalculator.GetFreshnessLifetime(updatedEntry));
            RecordValidationMetrics(request, revalidated, stopwatch, metricsEnabled);
            return revalidated;
        }

        if (ShouldCacheResponse(request, upstreamResponse, options, advancedDirectives, responseDirectives, requestDirectives))
        {
            try
            {
                var cacheEntry = await HttpCacheEntry.FromResponseAsync(request, upstreamResponse).ConfigureAwait(false);
                await StoreCacheEntryAsync(request, cacheKey, cacheEntry, options, variation, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Cached response for {Uri}, size: {Size} bytes", request.RequestUri, cacheEntry.Content.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache response for {Uri}", request.RequestUri);
            }
        }

        if (diagnostics.AddDiagnosticHeaders && !upstreamResponse.Headers.Contains("X-Cache"))
        {
            upstreamResponse.Headers.TryAddWithoutValidation("X-Cache", "MISS");
        }

        RecordMissMetrics(request, upstreamResponse, stopwatch, metricsEnabled);
        return upstreamResponse;
    }

    private bool IsMethodCacheable(HttpMethod method, HttpCacheOptions options)
        => options.CacheableMethods.Contains(method);

    private static bool ShouldBypassCache(CacheBehaviorOptions behavior, RequestCacheDirectives directives)
    {
        if (directives.NoCache || directives.NoStore)
        {
            return true;
        }

        if (!behavior.RespectOnlyIfCached && directives.OnlyIfCached)
        {
            return true;
        }

        return false;
    }

    private string GenerateCacheKey(HttpRequestMessage request, HttpCacheOptions options, ContentNegotiationHandler.AcceptedContent acceptedContent)
    {
        if (options.CacheKeyGenerator is { } custom)
        {
            return custom(request);
        }

        var builder = new StringBuilder();
        builder.Append(request.Method.Method);
        builder.Append(':');
        builder.Append(request.RequestUri);

        foreach (var header in options.Variation.AdditionalVaryHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(':');
            builder.Append(NormalizeHeaderName(header));
            builder.Append('=');
            builder.Append(GetHeaderValue(request, header));
        }

        if (!options.Behavior.IsSharedCache && request.Headers.Authorization is { } auth)
        {
            builder.Append(':');
            builder.Append(HashValue(auth.ToString()));
        }

        return builder.ToString();
    }

    private async ValueTask<HttpCacheEntry?> GetCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        CacheBehaviorOptions behavior,
        CacheVariationOptions variation,
        ContentNegotiationHandler negotiationHandler,
        ContentNegotiationHandler.AcceptedContent acceptedContent,
        CancellationToken cancellationToken)
    {
        var entry = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (entry == null)
        {
            return null;
        }

        if (!behavior.RespectVary)
        {
            // When Vary headers are disabled, return the entry without content negotiation checks
            return entry;
        }

        if (entry.VaryHeaders == null || entry.VaryHeaders.Length == 0)
        {
            return negotiationHandler.IsAcceptable(entry, acceptedContent) ? entry : null;
        }

        var varyKey = _varyKeyGenerator.GenerateKey(request, entry.VaryHeaders);
        if (varyKey.StartsWith("UNCACHEABLE", StringComparison.Ordinal))
        {
            return null;
        }

        var varyEntry = await _storage.GetAsync(varyKey, cancellationToken).ConfigureAwait(false);
        if (varyEntry == null)
        {
            return null;
        }

        return negotiationHandler.IsAcceptable(varyEntry, acceptedContent) ? varyEntry : null;
    }

    private async ValueTask StoreCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        CacheVariationOptions variation,
        CancellationToken cancellationToken)
    {
        if (entry.VaryHeaders == null || entry.VaryHeaders.Length == 0 || !options.Behavior.RespectVary)
        {
            VariantIndex.TryRemove(baseKey, out _);
            await _storage.SetAsync(baseKey, entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        var varyKey = _varyKeyGenerator.GenerateKey(request, entry.VaryHeaders);
        if (varyKey.StartsWith("UNCACHEABLE", StringComparison.Ordinal))
        {
            return;
        }

        await _storage.SetAsync(baseKey, entry, cancellationToken).ConfigureAwait(false);
        await _storage.SetAsync(varyKey, entry, cancellationToken).ConfigureAwait(false);

        if (variation.EnableMultipleVariants)
        {
            await EnforceVariantLimitAsync(baseKey, varyKey, variation.MaxVariantsPerUrl, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (VariantIndex.TryRemove(baseKey, out var queue))
            {
                while (queue.TryDequeue(out var existing))
                {
                    if (!string.Equals(existing, varyKey, StringComparison.Ordinal))
                    {
                        await _storage.RemoveAsync(existing, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async ValueTask EnforceVariantLimitAsync(string baseKey, string varyKey, int maxVariants, CancellationToken cancellationToken)
    {
        if (maxVariants <= 0)
        {
            return;
        }

        var queue = VariantIndex.GetOrAdd(baseKey, _ => new ConcurrentQueue<string>());
        queue.Enqueue(varyKey);

        while (queue.Count > maxVariants && queue.TryDequeue(out var evicted))
        {
            if (!string.Equals(evicted, varyKey, StringComparison.Ordinal))
            {
                await _storage.RemoveAsync(evicted, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool ShouldCacheResponse(
        HttpRequestMessage request,
        HttpResponseMessage response,
        HttpCacheOptions options,
        AdvancedCacheDirectives advancedDirectives,
        AdvancedCacheDirectives.ResponseDirectives directives,
        RequestCacheDirectives requestDirectives)
    {
        if (response.Content?.Headers.ContentLength.HasValue == true &&
            options.Storage.MaxResponseSize > 0 &&
            response.Content.Headers.ContentLength.Value > options.Storage.MaxResponseSize)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode &&
            response.StatusCode != HttpStatusCode.NotModified &&
            response.StatusCode != HttpStatusCode.MovedPermanently &&
            response.StatusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        if (requestDirectives.NoStore)
        {
            return false;
        }

        if (options.Behavior.RespectCacheControl && response.Headers.CacheControl?.NoStore == true)
        {
            return false;
        }

        if (!advancedDirectives.IsCacheable(directives, options.Behavior.IsSharedCache))
        {
            return false;
        }

        return HasFreshnessInfo(response, options) || HasValidationInfo(response);
    }

    private static bool HasFreshnessInfo(HttpResponseMessage response, HttpCacheOptions options)
    {
        return response.Headers.CacheControl?.MaxAge != null ||
               response.Content?.Headers.Expires != null ||
               (response.Content?.Headers.LastModified != null && options.Freshness.AllowHeuristicFreshness);
    }

    private static bool HasValidationInfo(HttpResponseMessage response)
    {
        return response.Headers.ETag != null || response.Content?.Headers.LastModified != null;
    }

    private HttpResponseMessage CreateCacheHitResponse(HttpCacheEntry entry, string cacheStatus, HttpCacheOptions options, TimeSpan? freshnessLifetime)
    {
        var response = entry.ToHttpResponse();

        if (options.Diagnostics.AddDiagnosticHeaders)
        {
            if (!response.Headers.Contains("X-Cache"))
            {
                response.Headers.TryAddWithoutValidation("X-Cache", cacheStatus);
            }

            response.Headers.TryAddWithoutValidation("X-Cache-Age", entry.GetAge().TotalSeconds.ToString("0"));

            if (freshnessLifetime.HasValue)
            {
                var ttl = freshnessLifetime.Value - entry.GetAge();
                response.Headers.TryAddWithoutValidation("X-Cache-TTL", Math.Max(0, ttl.TotalSeconds).ToString("0"));
            }
        }

        if (options.Behavior.EnableWarningHeaders && cacheStatus.StartsWith("STALE", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.TryAddWithoutValidation("Warning", cacheStatus.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? "111 - \"Revalidated stale response\"" : "110 - \"Response is stale\"");
        }

        return response;
    }

    private static void AddConditionalHeaders(HttpRequestMessage request, HttpCacheEntry cachedEntry)
    {
        if (!string.IsNullOrEmpty(cachedEntry.ETag))
        {
            request.Headers.IfNoneMatch.Clear();
            request.Headers.IfNoneMatch.ParseAdd(cachedEntry.ETag);
        }

        if (cachedEntry.LastModified.HasValue)
        {
            request.Headers.IfModifiedSince = cachedEntry.LastModified.Value;
        }
    }

    private Task QueueRevalidationAsync(HttpRequestMessage originalRequest, HttpCacheEntry cachedEntry, string baseKey, HttpCacheOptions options)
    {
        return Task.Run(async () =>
        {
            using var revalidationCts = options.Behavior.BackgroundRevalidationTimeout > TimeSpan.Zero
                ? new CancellationTokenSource(options.Behavior.BackgroundRevalidationTimeout)
                : new CancellationTokenSource();

            try
            {
                await RevalidateCacheEntry(originalRequest, cachedEntry, baseKey, options, revalidationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (revalidationCts.IsCancellationRequested)
            {
                _logger.LogDebug("Background revalidation timed out for {Uri}", originalRequest.RequestUri);
            }
        }, CancellationToken.None);
    }

    private async Task RevalidateCacheEntry(HttpRequestMessage originalRequest, HttpCacheEntry cachedEntry, string baseCacheKey, HttpCacheOptions options, CancellationToken cancellationToken)
    {
        var requestCopy = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri);
        foreach (var header in originalRequest.Headers)
        {
            requestCopy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        AddConditionalHeaders(requestCopy, cachedEntry);

        try
        {
            var response = await base.SendAsync(requestCopy, cancellationToken).ConfigureAwait(false);
            var variation = options.Variation;
            var advancedDirectives = new AdvancedCacheDirectives(options.Behavior);
            var requestDirectives = RequestCacheDirectives.Parse(originalRequest);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var updatedEntry = cachedEntry.WithUpdatedHeaders(response);
                await StoreCacheEntryAsync(originalRequest, baseCacheKey, updatedEntry, options, variation, cancellationToken).ConfigureAwait(false);
                _metrics?.RecordValidation(0);
            }
            else
            {
                var responseDirectives = advancedDirectives.ParseResponse(response);

                if (ShouldCacheResponse(originalRequest, response, options, advancedDirectives, responseDirectives, requestDirectives))
                {
                    var newEntry = await HttpCacheEntry.FromResponseAsync(originalRequest, response).ConfigureAwait(false);
                    await StoreCacheEntryAsync(originalRequest, baseCacheKey, newEntry, options, variation, cancellationToken).ConfigureAwait(false);
                }
            }

            response.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Background revalidation canceled for {Uri}", originalRequest.RequestUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background revalidation failed for {Uri}", originalRequest.RequestUri);
        }
        finally
        {
            requestCopy.Dispose();
        }
    }
            private void RecordHitMetrics(HttpRequestMessage request, HttpResponseMessage response, Stopwatch? stopwatch, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        stopwatch?.Stop();
        var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _metrics!.RecordHit((long)elapsed);
        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
    }

    private void RecordMissMetrics(HttpRequestMessage request, HttpResponseMessage response, Stopwatch? stopwatch, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        stopwatch?.Stop();
        var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _metrics!.RecordMiss((long)elapsed);
        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
    }

    private void RecordStaleMetrics(HttpRequestMessage request, HttpResponseMessage response, Stopwatch? stopwatch, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        stopwatch?.Stop();
        var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _metrics!.RecordStaleServed((long)elapsed);
        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
    }

    private void RecordValidationMetrics(HttpRequestMessage request, HttpResponseMessage response, Stopwatch? stopwatch, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        stopwatch?.Stop();
        var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _metrics!.RecordValidation((long)elapsed);
        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
    }

    private void RecordBypassMetrics(HttpRequestMessage request, HttpResponseMessage response, Stopwatch? stopwatch, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        stopwatch?.Stop();
        var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _metrics!.RecordBypass((long)elapsed);
        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
    }

    private static string NormalizeHeaderName(string headerName)
    {
        var parts = headerName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var span = parts[i].AsSpan();
            parts[i] = span.Length switch
            {
                0 => string.Empty,
                1 => char.ToUpper(span[0]).ToString(),
                _ => char.ToUpper(span[0]) + span[1..].ToString().ToLowerInvariant()
            };
        }

        return string.Join('-', parts);
    }

    private static string GetHeaderValue(HttpRequestMessage request, string headerName)
    {
        if (request.Headers.TryGetValues(headerName, out var requestValues))
        {
            return string.Join(",", requestValues);
        }

        if (request.Content?.Headers.TryGetValues(headerName, out var contentValues) == true)
        {
            return string.Join(",", contentValues);
        }

        return string.Empty;
    }

    private static string HashValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
    }
}





