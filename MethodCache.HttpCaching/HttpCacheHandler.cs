using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Net;
using System.Text;
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
internal sealed class HttpCacheHandler : DelegatingHandler
{
    private const string UncacheableVaryKeyPrefix = "UNCACHEABLE";

    private readonly HttpCacheClientState _state;
    private readonly IHttpCacheStorage _storage;
    private readonly IOptionsMonitor<HttpCacheOptions> _optionsMonitor;
    private readonly VaryHeaderCacheKeyGenerator _varyKeyGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly IHttpCacheMetrics? _metrics;
    private readonly ILogger<HttpCacheHandler> _logger;

    public HttpCacheHandler(
        HttpCacheClientState state,
        IHttpCacheStorage storage,
        IOptionsMonitor<HttpCacheOptions> optionsMonitor,
        VaryHeaderCacheKeyGenerator varyKeyGenerator,
        TimeProvider timeProvider,
        ILogger<HttpCacheHandler> logger,
        IHttpCacheMetrics? metrics = null)
    {
        _state = state;
        _storage = storage;
        _optionsMonitor = optionsMonitor;
        _varyKeyGenerator = varyKeyGenerator;
        _timeProvider = timeProvider;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.Get(_state.Name);
        var behavior = options.Behavior;
        var variation = options.Variation;
        var diagnostics = options.Diagnostics;
        var metricsEnabled = options.Metrics.EnableMetrics && _metrics != null;

        var stopwatch = metricsEnabled ? Stopwatch.StartNew() : null;

        var requestDirectives = RequestCacheDirectives.Parse(request);

        if (!IsMethodCacheable(request.Method, options) || ShouldBypassCache(behavior, requestDirectives))
        {
            var bypassResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsUnsafeMethod(request.Method) && (int)bypassResponse.StatusCode is >= 200 and < 400)
            {
                await InvalidateAfterUnsafeRequestAsync(request, bypassResponse, options, cancellationToken).ConfigureAwait(false);
            }

            RecordBypassMetrics(request, bypassResponse, stopwatch, metricsEnabled);
            return bypassResponse;
        }

        var negotiationHandler = new ContentNegotiationHandler(variation);
        var acceptedContent = negotiationHandler.ParseAcceptHeader(request);
        var freshnessCalculator = new FreshnessCalculator(behavior, options.Freshness, _timeProvider);
        var advancedDirectives = new AdvancedCacheDirectives(behavior);

        var cacheKey = GenerateCacheKey(request, options);
        var cachedEntry = await GetCacheEntryAsync(request, cacheKey, behavior, negotiationHandler, acceptedContent, cancellationToken).ConfigureAwait(false);

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
                var hit = CreateCacheHitResponse(request, cachedEntry, "HIT", options, freshnessCalculator);
                RecordHitMetrics(request, hit, stopwatch, metricsEnabled);
                return hit;
            }

            if (freshnessLifetime.HasValue && currentAge > freshnessLifetime.Value && freshnessCalculator.CanUseStaleWhileRevalidate(cachedEntry))
            {
                var stale = CreateCacheHitResponse(request, cachedEntry, "STALE", options, freshnessCalculator);
                RecordStaleMetrics(request, stale, stopwatch, metricsEnabled);

                if (_state.RevalidationsInFlight.TryAdd(cacheKey, 0))
                {
                    _ = QueueRevalidationAsync(request, cachedEntry, cacheKey, options);
                }
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
            var staleError = CreateCacheHitResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshnessCalculator);
            RecordStaleMetrics(request, staleError, stopwatch, metricsEnabled);
            return staleError;
        }

        var responseDirectives = advancedDirectives.ParseResponse(upstreamResponse);

        if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
        {
            _logger.LogDebug("Received 304 Not Modified for {Uri}, updating cache", request.RequestUri);
            var updatedEntry = cachedEntry.WithUpdatedHeaders(upstreamResponse, _timeProvider.GetUtcNow());
            await StoreCacheEntryAsync(request, cacheKey, updatedEntry, options, freshnessCalculator, cancellationToken).ConfigureAwait(false);

            upstreamResponse.Dispose();
            var revalidated = CreateCacheHitResponse(request, updatedEntry, "REVALIDATED", options, freshnessCalculator);
            RecordValidationMetrics(request, revalidated, stopwatch, metricsEnabled);
            return revalidated;
        }

        if (ShouldCacheResponse(upstreamResponse, options, advancedDirectives, responseDirectives, requestDirectives))
        {
            try
            {
                var cacheEntry = await HttpCacheEntry.FromResponseAsync(request, upstreamResponse, _timeProvider.GetUtcNow()).ConfigureAwait(false);
                await StoreCacheEntryAsync(request, cacheKey, cacheEntry, options, freshnessCalculator, cancellationToken).ConfigureAwait(false);
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

    private static bool IsMethodCacheable(HttpMethod method, HttpCacheOptions options)
        => options.CacheableMethods.Contains(method);

    // RFC 9110 section 9.2.1: GET, HEAD, OPTIONS and TRACE are safe; everything else may change the resource.
    private static bool IsUnsafeMethod(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;

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

    /// <summary>
    /// RFC 9111 section 4.4: a successful unsafe request invalidates the cached responses for its target URI and for
    /// same-origin <c>Location</c> and <c>Content-Location</c> URIs.
    /// </summary>
    private async ValueTask InvalidateAfterUnsafeRequestAsync(HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } target)
        {
            return;
        }

        var uris = new List<Uri>(3) { target };
        AddSameOrigin(uris, target, response.Headers.Location);
        AddSameOrigin(uris, target, response.Content?.Headers.ContentLocation);

        foreach (var uri in uris)
        {
            foreach (var method in options.CacheableMethods)
            {
                using var probe = new HttpRequestMessage(method, uri);
                foreach (var header in request.Headers)
                {
                    probe.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                var key = GenerateCacheKey(probe, options);
                await RemoveWithVariantsAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Invalidated cached responses for {Uri} after {Method}", target, request.Method);
    }

    private static void AddSameOrigin(List<Uri> uris, Uri target, Uri? candidate)
    {
        if (candidate == null)
        {
            return;
        }

        var absolute = candidate.IsAbsoluteUri ? candidate : new Uri(target, candidate);
        if (Uri.Compare(absolute, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
            !uris.Contains(absolute))
        {
            uris.Add(absolute);
        }
    }

    private async ValueTask RemoveWithVariantsAsync(string baseKey, CancellationToken cancellationToken)
    {
        var existing = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            return;
        }

        await RemoveKeysAsync(existing.VariantKeys, cancellationToken).ConfigureAwait(false);
        await _storage.RemoveAsync(baseKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveKeysAsync(IEnumerable<string>? keys, CancellationToken cancellationToken)
    {
        if (keys == null)
        {
            return;
        }

        foreach (var key in keys)
        {
            await _storage.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    private string GenerateCacheKey(HttpRequestMessage request, HttpCacheOptions options)
    {
        if (options.CacheKeyGenerator is { } custom)
        {
            return _state.Name + ":" + custom(request);
        }

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        sha256.AppendData(Encoding.UTF8.GetBytes(request.Method.Method));
        sha256.AppendData(Encoding.UTF8.GetBytes(request.RequestUri?.ToString() ?? string.Empty));

        foreach (var header in options.Variation.AdditionalVaryHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            sha256.AppendData(Encoding.UTF8.GetBytes(NormalizeHeaderName(header)));
            sha256.AppendData(Encoding.UTF8.GetBytes(GetHeaderValue(request, header)));
        }

        // A private cache keeps each caller's responses apart
        if (!options.Behavior.IsSharedCache && request.Headers.Authorization is { } auth)
        {
            sha256.AppendData(Encoding.UTF8.GetBytes(auth.ToString()));
        }

        return _state.Name + ":" + Convert.ToBase64String(sha256.GetHashAndReset());
    }

    /// <summary>
    /// The variant key starts with the base key, so a variant can only be found through its own URL, method and caller.
    /// </summary>
    private string? GenerateVariantKey(HttpRequestMessage request, string baseKey, string[] varyHeaders)
    {
        var varyKey = _varyKeyGenerator.GenerateKey(request, varyHeaders);
        return varyKey.StartsWith(UncacheableVaryKeyPrefix, StringComparison.Ordinal) ? null : baseKey + "|" + varyKey;
    }

    private async ValueTask<HttpCacheEntry?> GetCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        CacheBehaviorOptions behavior,
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

        var variantKey = GenerateVariantKey(request, baseKey, entry.VaryHeaders);
        if (variantKey == null)
        {
            return null;
        }

        var variant = await _storage.GetAsync(variantKey, cancellationToken).ConfigureAwait(false);
        if (variant == null)
        {
            return null;
        }

        return negotiationHandler.IsAcceptable(variant, acceptedContent) ? variant : null;
    }

    private async ValueTask StoreCacheEntryAsync(
        HttpRequestMessage request,
        string baseKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        FreshnessCalculator freshnessCalculator,
        CancellationToken cancellationToken)
    {
        var timeToLive = GetTimeToLive(entry, options, freshnessCalculator);
        if (timeToLive <= TimeSpan.Zero)
        {
            return;
        }

        var previous = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);

        if (entry.VaryHeaders == null || entry.VaryHeaders.Length == 0 || !options.Behavior.RespectVary)
        {
            await RemoveKeysAsync(previous?.VariantKeys, cancellationToken).ConfigureAwait(false);
            await _storage.SetAsync(baseKey, entry with { VariantKeys = null }, timeToLive, cancellationToken).ConfigureAwait(false);
            return;
        }

        var variantKey = GenerateVariantKey(request, baseKey, entry.VaryHeaders);
        if (variantKey == null)
        {
            return;
        }

        // The base entry keeps the variant list, oldest first, so it lives and dies with the cached URL
        var maxVariants = options.Variation.EnableMultipleVariants ? Math.Max(1, options.Variation.MaxVariantsPerUrl) : 1;
        var variants = previous?.VariantKeys?.Where(k => !string.Equals(k, variantKey, StringComparison.Ordinal)).ToList() ?? new List<string>();
        variants.Add(variantKey);

        var evicted = variants.Count > maxVariants ? variants.GetRange(0, variants.Count - maxVariants) : null;
        if (evicted != null)
        {
            variants.RemoveRange(0, evicted.Count);
            await RemoveKeysAsync(evicted, cancellationToken).ConfigureAwait(false);
        }

        await _storage.SetAsync(variantKey, entry with { VariantKeys = null }, timeToLive, cancellationToken).ConfigureAwait(false);
        await _storage.SetAsync(baseKey, entry with { VariantKeys = variants.ToArray() }, timeToLive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps an entry for its remaining freshness, plus <see cref="CacheFreshnessOptions.StaleRetention"/> when it
    /// can still be used stale: revalidated with its validators or served by stale-while-revalidate / stale-if-error.
    /// </summary>
    private static TimeSpan GetTimeToLive(HttpCacheEntry entry, HttpCacheOptions options, FreshnessCalculator freshnessCalculator)
    {
        var remaining = (freshnessCalculator.GetFreshnessLifetime(entry) ?? TimeSpan.Zero) - freshnessCalculator.GetCurrentAge(entry);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var usableWhenStale = entry.CanValidate() ||
                              options.Behavior.EnableStaleWhileRevalidate ||
                              options.Behavior.EnableStaleIfError;

        return usableWhenStale ? remaining + options.Freshness.StaleRetention : remaining;
    }

    private static bool ShouldCacheResponse(
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
        return ((options.Behavior.IsSharedCache && response.Headers.CacheControl?.SharedMaxAge != null) ||
                response.Headers.CacheControl?.MaxAge != null) ||
               response.Content?.Headers.Expires != null ||
               (response.Content?.Headers.LastModified != null && options.Freshness.AllowHeuristicFreshness);
    }

    private static bool HasValidationInfo(HttpResponseMessage response)
    {
        return response.Headers.ETag != null || response.Content?.Headers.LastModified != null;
    }

    private static HttpResponseMessage CreateCacheHitResponse(HttpRequestMessage request, HttpCacheEntry entry, string cacheStatus, HttpCacheOptions options, FreshnessCalculator freshnessCalculator)
    {
        var response = entry.ToHttpResponse(request);
        var age = freshnessCalculator.GetCurrentAge(entry);

        // RFC 9111 section 5.1: a response served from cache carries its current age
        response.Headers.Age = TimeSpan.FromSeconds(Math.Floor(age.TotalSeconds));

        if (options.Diagnostics.AddDiagnosticHeaders)
        {
            if (!response.Headers.Contains("X-Cache"))
            {
                response.Headers.TryAddWithoutValidation("X-Cache", cacheStatus);
            }

            response.Headers.TryAddWithoutValidation("X-Cache-Age", age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));

            if (freshnessCalculator.GetFreshnessLifetime(entry) is { } freshnessLifetime)
            {
                var ttl = freshnessLifetime - age;
                response.Headers.TryAddWithoutValidation("X-Cache-TTL", Math.Max(0, ttl.TotalSeconds).ToString("0", CultureInfo.InvariantCulture));
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
            finally
            {
                _state.RevalidationsInFlight.TryRemove(baseKey, out _);
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
            var freshnessCalculator = new FreshnessCalculator(options.Behavior, options.Freshness, _timeProvider);
            var advancedDirectives = new AdvancedCacheDirectives(options.Behavior);
            var requestDirectives = RequestCacheDirectives.Parse(originalRequest);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var updatedEntry = cachedEntry.WithUpdatedHeaders(response, _timeProvider.GetUtcNow());
                await StoreCacheEntryAsync(requestCopy, baseCacheKey, updatedEntry, options, freshnessCalculator, cancellationToken).ConfigureAwait(false);
                _metrics?.RecordValidation(0);
            }
            else
            {
                var responseDirectives = advancedDirectives.ParseResponse(response);

                if (ShouldCacheResponse(response, options, advancedDirectives, responseDirectives, requestDirectives))
                {
                    var newEntry = await HttpCacheEntry.FromResponseAsync(requestCopy, response, _timeProvider.GetUtcNow()).ConfigureAwait(false);
                    await StoreCacheEntryAsync(requestCopy, baseCacheKey, newEntry, options, freshnessCalculator, cancellationToken).ConfigureAwait(false);
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
        if (string.IsNullOrEmpty(headerName))
        {
            return string.Empty;
        }

        // Use Span to avoid allocations for small header names; fall back to heap for large ones
        const int StackAllocThreshold = 128;
        Span<char> buffer = headerName.Length <= StackAllocThreshold
            ? stackalloc char[headerName.Length]
            : new char[headerName.Length];
        headerName.AsSpan().CopyTo(buffer);

        var capitalizeNext = true;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '-')
            {
                capitalizeNext = true;
            }
            else if (capitalizeNext)
            {
                buffer[i] = char.ToUpperInvariant(buffer[i]);
                capitalizeNext = false;
            }
            else
            {
                buffer[i] = char.ToLowerInvariant(buffer[i]);
            }
        }

        return new string(buffer);
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
}





